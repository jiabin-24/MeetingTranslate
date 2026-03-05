using Azure.Communication.CallAutomation;
using EchoBot.Util;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

public sealed class AcsMediaWebSocketHandler()
{
    private readonly ILogger _logger = ServiceLocator.GetRequiredService<ILogger<AcsMediaWebSocketHandler>>();

    // Channel writer exposed so external producers (TTS) can push PCM bytes into the outbound pipeline.
    // The writer is created per active call/connection when AudioMetadata arrives.
    private ChannelWriter<byte[]>? _ttsWriter;

    public async Task RunAsync(WebSocket ws, CancellationToken ct)
    {
        _logger.LogInformation("WebSocket connected. State={State}", ws.State);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? sendLoop = null;

        // Receive loop
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        try
        {
            while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("WebSocket close received: {Status} {Desc}", result.CloseStatus, result.CloseStatusDescription);
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    // For this .NET SDK flow we expect text frames which contain a base64-encoded payload.
                    // If you see Binary frames, your endpoint might be using a different transport/sample.
                    _logger.LogWarning("Unexpected MessageType={Type}. Ignoring.", result.MessageType);
                    continue;
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (!result.EndOfMessage)
                    continue;

                string payload = sb.ToString();
                sb.Clear();

                // Parse streaming payload.
                StreamingData streaming = StreamingData.Parse(payload);

                switch (streaming)
                {
                    case AudioMetadata meta:
                        _logger.LogInformation("AudioMetadata: sampleRate={SampleRate}, channels={Channels}, encoding={Encoding}",
                            meta.SampleRate, meta.Channels, meta.Encoding);

                        // Create a channel for external TTS producers to push PCM data.
                        var channel = Channel.CreateUnbounded<byte[]>();
                        _ttsWriter = channel.Writer;

                        // Start send loop which will consume from the channel and send frames to ACS.
                        sendLoop ??= Task.Run(() => SendGeneratedAudioAsync(ws, meta, channel.Reader, cts.Token), ct);
                        break;

                    case AudioData audio:
                        break;

                    case DtmfData dtmf:
                        break;

                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket handler crashed");
        }
        finally
        {
            try
            {
                cts.Cancel();
                if (sendLoop is not null)
                {
                    await Task.WhenAny(sendLoop, Task.Delay(1000));
                }
            }
            catch { /* ignore */ }

            if (ws.State == WebSocketState.Open)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing", CancellationToken.None);
            }

            _logger.LogInformation("WebSocket disconnected.");
        }
    }

    /// <summary>
    /// Consume PCM data from <paramref name="reader"/> and send to ACS in fixed-size frames.
    /// External code should push raw PCM bytes (16-bit LE, interleaved if multi-channel) into the channel writer.
    /// </summary>
    private async Task SendGeneratedAudioAsync(WebSocket ws, AudioMetadata meta, ChannelReader<byte[]> reader, CancellationToken ct)
    {
        int sampleRate = meta.SampleRate; // documented as 16kHz or 24kHz
        int frameMs = 20;
        int samplesPerFrame = sampleRate * frameMs / 1000;
        int bytesPerSample = 2; // 16-bit PCM
        int channels = Math.Max(1, (int)meta.Channels);
        int frameBytes = samplesPerFrame * bytesPerSample * channels;

        _logger.LogInformation("Starting outbound audio consumer: sampleRate={SampleRate}, channels={Channels}, samplesPerFrame={SamplesPerFrame}, frameBytes={FrameBytes}",
            sampleRate, channels, samplesPerFrame, frameBytes);

        var buffer = new MemoryStream();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long nextDueMs = 0;

        try
        {
            await foreach (var chunk in reader.ReadAllAsync(ct))
            {
                // append incoming PCM chunk
                buffer.Write(chunk, 0, chunk.Length);

                // while we have at least one full frame, send it
                while (buffer.Length >= frameBytes)
                {
                    buffer.Position = 0;
                    byte[] frame = new byte[frameBytes];
                    int read = buffer.Read(frame, 0, frameBytes);

                    // shift remaining bytes to start of stream
                    var remaining = new byte[(int)(buffer.Length - frameBytes)];
                    int remRead = buffer.Read(remaining, 0, remaining.Length);
                    buffer.SetLength(0);
                    if (remaining.Length > 0)
                        buffer.Write(remaining, 0, remRead);

                    // send frame
                    string outbound = OutStreamingData.GetAudioDataForOutbound(frame);
                    await SendTextAsync(ws, outbound, ct);

                    // pace
                    nextDueMs += frameMs;
                    long delay = nextDueMs - stopwatch.ElapsedMilliseconds;
                    if (delay > 0)
                    {
                        await Task.Delay((int)delay, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in outbound audio consumer");
        }
        finally
        {
            // Send stop if socket still open
            if (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                string stop = OutStreamingData.GetStopAudioForOutbound();
                try { await SendTextAsync(ws, stop, CancellationToken.None); } catch { /* ignore */ }
            }

            _logger.LogInformation("Outbound audio finished.");
        }
    }

    /// <summary>
    /// Try to obtain the channel writer that external producers can use to push PCM frames.
    /// Returns false if no writer is currently available (e.g. before AudioMetadata arrives.
    /// </summary>
    public bool TryGetTtsWriter(out ChannelWriter<byte[]>? writer)
    {
        writer = _ttsWriter;
        return writer != null;
    }

    /// <summary>
    /// Convenience helper for external callers to push PCM bytes into the active TTS channel.
    /// Returns false if no active writer exists or the write failed.
    /// </summary>
    public async ValueTask<bool> PushTtsFrameAsync(string threadId, byte[] pcm, CancellationToken ct)
    {
        var w = _ttsWriter;
        if (w is null)
            return false;

        try
        {
            await w.WriteAsync(pcm, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task SendTextAsync(WebSocket ws, string message, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}
