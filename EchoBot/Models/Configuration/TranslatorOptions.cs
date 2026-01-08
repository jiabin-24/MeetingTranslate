namespace MeetingTranscription.Models.Configuration
{
    public class TranslatorOptions
    {
        public string Key { get; set; }

        public string Region { get; set; }

        public string Endpoint { get; set; } = "https://api.cognitive.microsofttranslator.com";

        /// <summary>
        /// Gets or sets the language code used for speech recognition or synthesis.
        /// </summary>
        /// <remarks>The language code should follow the IETF BCP 47 standard (for example, "en-US" for
        /// U.S. English or "fr-FR" for French). The value determines the language context for speech processing
        /// operations.</remarks>
        public Dictionary<string, Dictionary<string, string>> Routing { get; set; }
    }
}
