using Microsoft.CognitiveServices.Speech;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Skype.Bots.Media;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace EchoBot.Util
{
    /// <summary>
    /// The utility class.
    /// </summary>
    internal static class Utilities
    {
        /// <summary>
        /// Helper function to create the audio buffers from file.
        /// Please make sure the audio file provided is PCM16Khz and the fileSizeInSec is the correct length.
        /// </summary>
        /// <param name="currentTick">The current clock tick.</param>
        /// <param name="replayed">Whether it's replayed.</param>
        /// <param name="logger">Graph logger.</param>
        /// <returns>The newly created list of <see cref="AudioMediaBuffer"/>.</returns>
        public static List<AudioMediaBuffer> CreateAudioMediaBuffers(AudioDataStream stream, long currentTick, ILogger logger)
        {
            var audioMediaBuffers = new List<AudioMediaBuffer>();
            var referenceTime = currentTick;

            // packet size of 20 ms
            var numberOfTicksInOneAudioBuffers = 20 * 10000;

            byte[] bytesToRead = new byte[640];

            // skipping the wav headers
            stream.SetPosition(44);
            while (stream.ReadData(bytesToRead) >= 640)
            {
                // here we want to create buffers of 20MS with PCM 16Khz
                IntPtr unmanagedBuffer = Marshal.AllocHGlobal(640);
                Marshal.Copy(bytesToRead, 0, unmanagedBuffer, 640);
                var audioBuffer = new AudioSendBuffer(unmanagedBuffer, 640, AudioFormat.Pcm16K, referenceTime);
                audioMediaBuffers.Add(audioBuffer);
                referenceTime += numberOfTicksInOneAudioBuffers;
            }

            logger.LogTrace($"created {audioMediaBuffers.Count} AudioMediaBuffers");
            return audioMediaBuffers;
        }

        /// <summary>
        /// Create a single AudioMediaBuffer from a byte array. Caller is responsible for disposing the returned buffer.
        /// </summary>
        public static AudioMediaBuffer CreateAudioMediaBuffer(byte[] buffer, long referenceTime, ILogger logger)
        {
            if (buffer == null || buffer.Length == 0)
                throw new ArgumentException("buffer must not be null or empty", nameof(buffer));

            var length = buffer.Length;
            IntPtr unmanagedBuffer = Marshal.AllocHGlobal(length);
            Marshal.Copy(buffer, 0, unmanagedBuffer, length);
            var audioBuffer = new AudioSendBuffer(unmanagedBuffer, length, AudioFormat.Pcm16K, referenceTime);
            logger.LogTrace("created 1 AudioMediaBuffer");
            return audioBuffer;
        }

        public static List<AudioMediaBuffer> CreateAudioMediaBuffers(byte[] buffer, long currentTick, ILogger logger)
        {
            var audioMediaBuffers = new List<AudioMediaBuffer>();
            var referenceTime = currentTick;

            // packet size of 20 ms
            var numberOfTicksInOneAudioBuffers = 20 * 10000;

            // here we want to create buffers of 20MS with PCM 16Khz
            IntPtr unmanagedBuffer = Marshal.AllocHGlobal(640);
            Marshal.Copy(buffer, 0, unmanagedBuffer, 640);
            var audioBuffer = new AudioSendBuffer(unmanagedBuffer, 640, AudioFormat.Pcm16K, referenceTime);
            audioMediaBuffers.Add(audioBuffer);
            referenceTime += numberOfTicksInOneAudioBuffers;

            logger.LogTrace($"created {audioMediaBuffers.Count} AudioMediaBuffers");
            return audioMediaBuffers;
        }

        /// <summary>
        /// Helper to search the certificate store by its thumbprint.
        /// </summary>
        /// <returns>Certificate if found.</returns>
        /// <exception cref="Exception">No certificate with thumbprint {CertificateThumbprint} was found in the machine store.</exception>
        public static X509Certificate2 GetCertificateFromStore(string certificateThumbprint)
        {

            X509Store store = new(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            try
            {
                X509Certificate2Collection certs = store.Certificates.Find(X509FindType.FindByThumbprint, certificateThumbprint, validOnly: false);

                if (certs.Count != 1)
                {
                    throw new Exception($"No certificate with thumbprint {certificateThumbprint} was found in the machine store.");
                }

                return certs[0];
            }
            finally
            {
                store.Close();
            }
        }
    }
}

