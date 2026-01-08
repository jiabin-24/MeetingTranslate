using Azure.AI.Agents.Persistent;
using Azure.Identity;
using EchoBot.Bot;
using EchoBot.Models;
using MeetingTranscription.Helpers;
using MeetingTranscription.Models.Configuration;
using MeetingTranscription.Services;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Teams;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Agents.AzureAI;
using Microsoft.SemanticKernel.ChatCompletion;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace MeetingTranscription.Bots
{
    public class TranscriptionBot : TeamsActivityHandler
    {
        /// <summary>
        /// Helper instance to make graph calls.
        /// </summary>
        private readonly GraphHelper _graphHelper;

        /// <summary>
        /// Stores the Azure configuration values.
        /// </summary>
        private readonly IOptions<AzureSettings> _azureSettings;

        private readonly AIServiceSettings _aiSettings;

        /// <summary>
        /// Store details of meeting transcript.
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _transcriptsDictionary;

        private readonly PersistentAgentsClient _agentClient;

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
        private readonly ICardFactory _cardFactory;

        /// <summary>
        /// The bot service
        /// </summary>
        private readonly IBotService _botService;

        private readonly ILogger _logger;

        /// <summary>
        /// Creates bot instance.
        /// </summary>
        /// <param name="azureSettings">Stores the Azure configuration values.</param>
        /// <param name="transcriptsDictionary">Store details of meeting transcript.</param>
        /// <param name="cardFactory">Instance of card factory to create adaptive cards.</param>
        /// <param name="botService">Join bot service</param>
        public TranscriptionBot(IOptions<AzureSettings> azureSettings, IOptions<AIServiceSettings> aiSettingsOpt, ConcurrentDictionary<string, string> transcriptsDictionary,
            ICardFactory cardFactory, IBotService botService, ILogger<TranscriptionBot> logger)
        {
            _transcriptsDictionary = transcriptsDictionary;
            _azureSettings = azureSettings;
            _graphHelper = new GraphHelper();
            _cardFactory = cardFactory;
            _botService = botService;
            _logger = logger;

            _aiSettings = aiSettingsOpt.Value;
            if (!string.IsNullOrEmpty(_aiSettings.AgentId))
                _agentClient = new PersistentAgentsClient(_aiSettings.ProjectEndpoint, new ClientSecretCredential(_aiSettings.TenantId, _aiSettings.ClientId, _aiSettings.ClientSecret));
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
            if (!string.IsNullOrEmpty(_aiSettings.AgentId))
            {
                await SendAIMessage(turnContext, cancellationToken);
                return;
            }
            var replyText = $"Echo: {turnContext.Activity.Text}";
            await turnContext.SendActivityAsync(MessageFactory.Text(replyText, replyText), cancellationToken);
        }

        private async Task SendAIMessage(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            var agentDefinition = await _agentClient.Administration.GetAgentAsync(_aiSettings.AgentId, cancellationToken);
            var agent = new AzureAIAgent(agentDefinition, _agentClient);
            var thread = new AzureAIAgentThread(_agentClient);

            // create the user message to send to the agent
            var message = new ChatMessageContent(AuthorRole.User, turnContext.Activity.Text);

            // invoke the agent and stream the responses to the user
            // Send an initial placeholder message and update it as chunks arrive so the user sees a streaming reply
            var initialActivity = MessageFactory.Text("Working on it...");
            var sendResponse = await turnContext.SendActivityAsync(initialActivity, cancellationToken);

            var aggregated = string.Empty;

            // Throttle settings: don't update more than once per interval to avoid hitting rate limits.
            var throttleInterval = TimeSpan.FromMilliseconds(300);
            var lastUpdate = DateTime.MinValue;

            await foreach (AgentResponseItem<StreamingChatMessageContent> agentResponse in agent.InvokeStreamingAsync(message, thread, cancellationToken: cancellationToken))
            {
                // aggregate partial content
                aggregated += agentResponse.Message.Content;

                var now = DateTime.UtcNow;

                // Only update the activity when throttle interval has passed
                if ((now - lastUpdate) >= throttleInterval)
                {
                    lastUpdate = now;

                    // build an update activity that targets the original sent message
                    var updateActivity = new Activity
                    {
                        Id = sendResponse.Id,
                        Type = ActivityTypes.Message,
                        Text = aggregated,
                        Conversation = turnContext.Activity.Conversation,
                        From = turnContext.Activity.Recipient,
                        Recipient = turnContext.Activity.From,
                        ChannelId = turnContext.Activity.ChannelId,
                        ServiceUrl = turnContext.Activity.ServiceUrl,
                    };

                    // update the original message so the user sees the response streaming
                    try
                    {
                        await turnContext.UpdateActivityAsync(updateActivity, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        // fallback to sending incremental messages if update isn't allowed
                        _logger.LogWarning($"Failed to update activity while streaming, sending incremental message. {ex.Message}");
                        await turnContext.SendActivityAsync(MessageFactory.Text(aggregated), cancellationToken);
                    }
                }
            }

            // Ensure final content is sent/updated after streaming completes
            try
            {
                var finalUpdate = new Activity
                {
                    Id = sendResponse.Id,
                    Type = ActivityTypes.Message,
                    Text = aggregated,
                    Conversation = turnContext.Activity.Conversation,
                    From = turnContext.Activity.Recipient,
                    Recipient = turnContext.Activity.From,
                    ChannelId = turnContext.Activity.ChannelId,
                    ServiceUrl = turnContext.Activity.ServiceUrl,
                };

                await turnContext.UpdateActivityAsync(finalUpdate, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to send final update activity while streaming, sending final message. {ex.Message}");
                await turnContext.SendActivityAsync(MessageFactory.Text(aggregated), cancellationToken);
            }
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
                _logger.LogError($"Error in OnTeamsMeetingStartAsync: {ex.Message}");
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

                _logger.LogInformation($"Meeting Ended: {meetingInfo.Details.MsGraphResourceId}");

                // End the bot's call in the meeting
                if (_botMeetingsDictionary.TryRemove(meeting.Id, out var threadId))
                    await _botService.EndCallByThreadIdAsync(threadId);

                await SendMeetingTranscriptions(meetingInfo, turnContext, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in OnTeamsMeetingEndAsync: {ex.Message}");
            }
        }

        private async Task SendMeetingTranscriptions(Microsoft.Bot.Schema.Teams.MeetingInfo meetingInfo, ITurnContext<IEventActivity> turnContext, CancellationToken cancellationToken)
        {
            try
            {
                // NEW: Get meeting organizer information when meeting ends
                var organizerId = await _graphHelper.GetMeetingOrganizerFromTeamsContextAsync(turnContext);
                if (!string.IsNullOrEmpty(organizerId))
                {
                    _logger.LogInformation($"Meeting organizer identified: {organizerId}");
                }

                // NEW: Use Teams context to find organizer and get transcripts
                var result = await _graphHelper.GetMeetingTranscriptionsAsync(meetingInfo.Details.MsGraphResourceId, organizerId);

                if (!string.IsNullOrEmpty(result))
                {
                    _transcriptsDictionary.AddOrUpdate(meetingInfo.Details.MsGraphResourceId, result, (key, newValue) => result);

                    var attachment = this._cardFactory.CreateAdaptiveCardAttachement(new { MeetingId = meetingInfo.Details.MsGraphResourceId });
                    await turnContext.SendActivityAsync(MessageFactory.Attachment(attachment), cancellationToken);

                    _logger.LogInformation($"Successfully retrieved and cached meeting transcript for {meetingInfo.Details.MsGraphResourceId}");
                }
                else
                {
                    var attachment = this._cardFactory.CreateNotFoundCardAttachement();
                    await turnContext.SendActivityAsync(MessageFactory.Attachment(attachment), cancellationToken);

                    _logger.LogInformation($"No transcript found for meeting {meetingInfo.Details.MsGraphResourceId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in OnTeamsMeetingEndAsync: {ex.Message}");
                // Send error card to user
                var errorAttachment = this._cardFactory.CreateNotFoundCardAttachement();
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
                            Url = $"{this._azureSettings.Value.AppBaseUrl}/home?meetingId={meetingId}",
                            Height = 600,
                            Width = 600,
                            Title = "Meeting Transcript",
                        },
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in OnTeamsTaskModuleFetchAsync: {ex.Message}");

                return new TaskModuleResponse
                {
                    Task = new TaskModuleContinueResponse
                    {
                        Type = "continue",
                        Value = new TaskModuleTaskInfo()
                        {
                            Url = this._azureSettings.Value.AppBaseUrl + "/home",
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