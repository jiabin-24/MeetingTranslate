using System.Text.Json.Serialization;

namespace EchoBot.Models
{
    public class Caption
    {
        public record SubscribeMessage(string Type, string MeetingId, string TargetLang);

        // 服务端推送给前端的字幕负载
        public record CaptionPayload(
            [property: JsonPropertyName("type")] string Type,          // "caption"
            [property: JsonPropertyName("meetingId")] string MeetingId,
            [property: JsonPropertyName("speaker")] string? Speaker,
            [property: JsonPropertyName("speakerId")] string? SpeakerId,
            [property: JsonPropertyName("sourceLang")] string SourceLang,          // 源语言，如 "en" / "zh"
            [property: JsonPropertyName("text")] Dictionary<string, string> Text,
            [property: JsonPropertyName("isFinal")] bool IsFinal,         // partial / final
            [property: JsonPropertyName("startMs")] long? StartMs,
            [property: JsonPropertyName("endMs")] long? EndMs,
            [property: JsonPropertyName("realStartMs")] long? RealStartMs
        );

        // 首条鉴权消息（示例）
        public record AuthMessage(string Type, string Token);
    }
}
