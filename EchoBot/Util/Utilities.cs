using Microsoft.CognitiveServices.Speech;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Skype.Bots.Media;
using NAudio.Wave;
using StackExchange.Redis;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace EchoBot.Util
{
    /// <summary>
    /// The utility class.
    /// </summary>
    internal static class Utilities
    {
        private const string ReleaseLockScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
        private const string SetValueIfOwnedScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('set', KEYS[1], ARGV[2], 'PX', ARGV[3]) else return nil end";

        public static Task<T> ExecuteWithDistributedLockAsync<T>(
            IConnectionMultiplexer mux,
            string lockKey,
            TimeSpan expiry,
            Func<string> lockFailedMessageFactory,
            Func<Task<T>> action)
        {
            return ExecuteWithDistributedLockAsync(
                mux,
                lockKey,
                expiry,
                onLockNotAcquired: () => throw new InvalidOperationException(lockFailedMessageFactory()),
                action: action);
        }

        /// <summary>
        /// 确保同一时间内（多实例）只有一个实例在执行操作
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="mux"></param>
        /// <param name="lockKey"></param>
        /// <param name="expiry"></param>
        /// <param name="onLockNotAcquired"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        public static async Task<T> ExecuteWithDistributedLockAsync<T>(
            IConnectionMultiplexer mux,
            string lockKey,
            TimeSpan expiry,
            Func<Task<T>> onLockNotAcquired,
            Func<Task<T>> action)
        {
            var lockToken = Guid.NewGuid().ToString("N");
            var database = mux.GetDatabase();

            var lockAcquired = await database.StringSetAsync(
                lockKey,
                lockToken,
                expiry,
                when: When.NotExists).ConfigureAwait(false);

            if (!lockAcquired)
            {
                return await onLockNotAcquired().ConfigureAwait(false);
            }

            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                await database.ScriptEvaluateAsync(
                    ReleaseLockScript,
                    new RedisKey[] { lockKey },
                    new RedisValue[] { lockToken }).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 确保多实例场景下，一个操作被执行后，不再继续被其他实例执行，直到过期时间到。适用于操作完成后结果可以被其他实例复用的场景。
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="mux"></param>
        /// <param name="stateKey"></param>
        /// <param name="pendingState"></param>
        /// <param name="pendingExpiry"></param>
        /// <param name="completedExpiry"></param>
        /// <param name="onStateAlreadyExists"></param>
        /// <param name="action"></param>
        /// <param name="completedStateSelector"></param>
        /// <returns></returns>
        public static async Task<T> ExecuteWithSharedStateAsync<T>(
            IConnectionMultiplexer mux,
            string stateKey,
            RedisValue pendingState,
            TimeSpan pendingExpiry,
            TimeSpan completedExpiry,
            Func<RedisValue, Task<T>> onStateAlreadyExists,
            Func<Task<T>> action,
            Func<T, RedisValue> completedStateSelector)
        {
            var db = mux.GetDatabase();
            var ownerToken = Guid.NewGuid().ToString("N");
            var ownedPendingState = $"{pendingState}|owner:{ownerToken}";
            var pendingStateSet = await db.StringSetAsync(
                stateKey,
                ownedPendingState,
                pendingExpiry,
                when: When.NotExists).ConfigureAwait(false);

            if (!pendingStateSet)
            {
                var existingState = await db.StringGetAsync(stateKey).ConfigureAwait(false);
                return await onStateAlreadyExists(existingState).ConfigureAwait(false);
            }

            T result;
            try
            {
                result = await action().ConfigureAwait(false);
            }
            catch
            {
                await db.ScriptEvaluateAsync(
                    ReleaseLockScript,
                    new RedisKey[] { stateKey },
                    new RedisValue[] { ownedPendingState }).ConfigureAwait(false);
                throw;
            }

            var setCompletedResult = await db.ScriptEvaluateAsync(
                SetValueIfOwnedScript,
                new RedisKey[] { stateKey },
                new RedisValue[]
                {
                    ownedPendingState,
                    completedStateSelector(result),
                    (long)completedExpiry.TotalMilliseconds
                }).ConfigureAwait(false);

            if (setCompletedResult.IsNull)
            {
                throw new InvalidOperationException($"State ownership lost for key '{stateKey}'.");
            }

            return result;
        }

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

        public static byte[] ConvertToPcm16Mono16k(byte[] input, int bytesRecorded, WaveFormat format, ref int leftoverCount, byte[] leftoverBuffer)
        {
            if (bytesRecorded <= 0)
            {
                return Array.Empty<byte>();
            }

            var bitsPerSample = format.BitsPerSample;
            var channels = Math.Max(1, format.Channels);
            var sampleRate = format.SampleRate;
            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat && bitsPerSample == 32;
            var bytesPerSample = bitsPerSample / 8;
            var frameSize = bytesPerSample * channels;

            if (frameSize <= 0)
            {
                return Array.Empty<byte>();
            }

            var merged = new byte[leftoverCount + bytesRecorded];
            if (leftoverCount > 0)
            {
                Buffer.BlockCopy(leftoverBuffer, 0, merged, 0, leftoverCount);
            }

            Buffer.BlockCopy(input, 0, merged, leftoverCount, bytesRecorded);

            var totalFrames = merged.Length / frameSize;
            var remainingBytes = merged.Length - (totalFrames * frameSize);
            if (remainingBytes > 0)
            {
                Buffer.BlockCopy(merged, merged.Length - remainingBytes, leftoverBuffer, 0, remainingBytes);
            }

            leftoverCount = remainingBytes;
            if (totalFrames == 0)
            {
                return Array.Empty<byte>();
            }

            var mono = new float[totalFrames];
            for (int frame = 0; frame < totalFrames; frame++)
            {
                float sum = 0;
                var frameOffset = frame * frameSize;
                for (int ch = 0; ch < channels; ch++)
                {
                    var sampleOffset = frameOffset + (ch * bytesPerSample);
                    float sample = 0;
                    if (isFloat)
                    {
                        sample = BitConverter.ToSingle(merged, sampleOffset);
                    }
                    else if (bitsPerSample == 16)
                    {
                        sample = BitConverter.ToInt16(merged, sampleOffset) / 32768f;
                    }

                    sum += sample;
                }

                mono[frame] = sum / channels;
            }

            var outSamples = (int)Math.Round((double)mono.Length * 16000 / sampleRate);
            if (outSamples <= 0)
            {
                return Array.Empty<byte>();
            }

            var output = new byte[outSamples * 2];
            for (int i = 0; i < outSamples; i++)
            {
                var srcPos = (double)i * sampleRate / 16000;
                var srcIdx = (int)srcPos;
                var frac = srcPos - srcIdx;

                var s0 = mono[Math.Min(srcIdx, mono.Length - 1)];
                var s1 = mono[Math.Min(srcIdx + 1, mono.Length - 1)];
                var sample = s0 + ((float)frac * (s1 - s0));
                sample = Math.Clamp(sample, -1.0f, 1.0f);

                var pcm = (short)Math.Round(sample * short.MaxValue);
                output[i * 2] = (byte)(pcm & 0xff);
                output[(i * 2) + 1] = (byte)((pcm >> 8) & 0xff);
            }

            return output;
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

        public static Dictionary<TKey, TValue> ConcatDictionary<TKey, TValue>(Dictionary<TKey, TValue> source, Dictionary<TKey, TValue> pairs)
            where TKey : notnull
        {
            if (pairs == null)
                return source;

            foreach (var kv in pairs)
            {
                source[kv.Key] = kv.Value;
            }
            return source;
        }
    }
}

