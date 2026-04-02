using Microsoft.AspNetCore.Http;
using Microsoft.Graph.Contracts;
using Microsoft.Graph.Models;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EchoBot.Models
{
    /// <summary>
    /// Gets the join information.
    /// </summary>
    public class JoinInfo
    {
        public static async Task<(string Payload, string? CallId)> ReadPayloadAndCallIdAsync(HttpRequest request)
        {
            request.EnableBuffering();

            string payload;
            using (var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
            {
                payload = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }

            return (payload, TryExtractCallId(payload));
        }

        private static string? TryExtractCallId(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                return FindCallId(doc.RootElement);
            }
            catch
            {
                return null;
            }
        }

        private static string? FindCallId(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            var callId = ExtractCallIdFromText(property.Value.GetString());
                            if (!string.IsNullOrWhiteSpace(callId))
                            {
                                return callId;
                            }
                        }

                        var nestedCallId = FindCallId(property.Value);
                        if (!string.IsNullOrWhiteSpace(nestedCallId))
                        {
                            return nestedCallId;
                        }
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        var callId = FindCallId(item);
                        if (!string.IsNullOrWhiteSpace(callId))
                        {
                            return callId;
                        }
                    }
                    break;
            }

            return null;
        }

        private static string? ExtractCallIdFromText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            const string marker = "/communications/calls/";
            var start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            var end = value.IndexOf('/', start);
            if (end < 0)
            {
                end = value.IndexOf('?', start);
            }
            if (end < 0)
            {
                end = value.Length;
            }

            if (end <= start)
            {
                return null;
            }

            return value[start..end];
        }

        /// <summary>
        /// Parse Join URL into its components.
        /// </summary>
        /// <param name="joinURL">Join URL from Team's meeting body.</param>
        /// <returns>Parsed data.</returns>
        /// <exception cref="ArgumentException">Join URL cannot be null or empty: {joinURL} - joinURL</exception>
        /// <exception cref="ArgumentException">Join URL cannot be parsed: {joinURL} - joinURL</exception>
        /// <exception cref="ArgumentException">Join URL is invalid: missing Tid - joinURL</exception>
        public static (ChatInfo, MeetingInfo) ParseJoinURL(string joinURL)
        {
            if (string.IsNullOrEmpty(joinURL))
            {
                throw new ArgumentException($"Join URL cannot be null or empty: {joinURL}", nameof(joinURL));
            }

            var decodedURL = WebUtility.UrlDecode(joinURL);

            var regex = new Regex("https://teams\\.microsoft\\.com.*/(?<thread>[^/]+)/(?<message>[^/]+)\\?context=(?<context>{.*})");
            var match = regex.Match(decodedURL);
            if (!match.Success)
            {
                throw new ArgumentException($"Join URL cannot be parsed: {joinURL}", nameof(joinURL));
            }

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(match.Groups["context"].Value));
            var ctxt = new DataContractJsonSerializer(typeof(Meeting)).ReadObject(stream) as Meeting;

            if (string.IsNullOrEmpty(ctxt!.Tid))
            {
                throw new ArgumentException("Join URL is invalid: missing Tid", nameof(joinURL));
            }

            var chatInfo = new ChatInfo
            {
                ThreadId = match.Groups["thread"].Value,
                MessageId = match.Groups["message"].Value,
                ReplyChainMessageId = ctxt.MessageId,
            };

            var meetingInfo = new OrganizerMeetingInfo
            {
                Organizer = new IdentitySet
                {
                    User = new Identity { Id = ctxt.Oid },
                },
            };
            meetingInfo.Organizer.User.SetTenantId(ctxt.Tid);

            return (chatInfo, meetingInfo);
        }
    }
}
