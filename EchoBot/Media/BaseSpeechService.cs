using EchoBot.Util;

namespace EchoBot.Media
{
    public class BaseSpeechService
    {
        /// <summary>
        /// The is the indicator if the media stream is running
        /// </summary>
        protected bool IsRunning = false;

        // Energy threshold (RMS) above which we consider this buffer as active speech for assigning speaker id
        protected const double SpeakerEnergyThreshold = 500.0;

        protected string CurrentSpeakerId = string.Empty;

        // Logger created for the runtime type so derived classes get a category with their actual type
        protected ILogger Logger;

        public BaseSpeechService()
        {
            Logger = ServiceLocator.GetRequiredService<ILoggerFactory>().CreateLogger(GetType().FullName ?? GetType().Name);
        }

        // Compute RMS energy from a 16-bit PCM buffer
        private static double ComputeRmsFrom16BitPcm(byte[] buffer, long bufferLength)
        {
            if (buffer == null || bufferLength <= 1) return 0.0;

            long sampleCount = bufferLength / 2; // 16-bit samples
            if (sampleCount == 0) return 0.0;

            double sumSquares = 0.0;

            for (long i = 0; i < sampleCount; i++)
            {
                int offset = (int)(i * 2);
                short sample = (short)(buffer[offset] | (buffer[offset + 1] << 8));
                double normalized = sample; // keep in int16 domain to compute RMS
                sumSquares += normalized * normalized;
            }

            double meanSquares = sumSquares / sampleCount;
            double rms = Math.Sqrt(meanSquares);
            return rms;
        }

        protected void SetCurrentSpeaker(string speakerId, byte[]? buffer, long bufferLength)
        {
            if (speakerId != null)
            {
                // Compute buffer energy (RMS) for 16-bit PCM and only assign speaker when above threshold
                try
                {
                    var rms = ComputeRmsFrom16BitPcm(buffer, bufferLength);
                    if (rms >= SpeakerEnergyThreshold)
                        CurrentSpeakerId = speakerId;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to compute audio energy, assigning speaker id by default");
                    CurrentSpeakerId = speakerId;
                }
            }
        }
    }
}
