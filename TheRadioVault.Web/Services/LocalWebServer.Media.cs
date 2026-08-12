using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using TheRadioVault.Web.Contracts;

namespace TheRadioVault.Web.Services;

public sealed partial class LocalWebServer
{
    private async Task<bool> TryHandleCanonicalMediaRouteAsync(
        Stream stream,
        string path,
        IReadOnlyDictionary<string, string> query,
        HttpRequest request,
        bool isHead,
        CancellationToken cancellationToken)
    {
        if (TryMatchCanonicalMediaManifest(path, out var manifestEpisodeId))
        {
            await HandleCanonicalMediaManifestAsync(stream, manifestEpisodeId, query, isHead, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchCanonicalMediaStart(path, out var startEpisodeId))
        {
            await HandleCanonicalMediaStartAsync(stream, request, startEpisodeId, query, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (TryMatchCanonicalMediaPart(path, out var mediaEpisodeId, out var mediaFileId))
        {
            await HandleCanonicalMediaPartAsync(stream, request, mediaEpisodeId, mediaFileId, query, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleArtworkAudioRouteAsync(
        Stream stream,
        string path,
        HttpRequest request,
        bool isHead,
        CancellationToken cancellationToken)
    {
        if (path.StartsWith("/artwork/", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(path[9..], out var artworkEpisodeId))
        {
            await HandleArtworkAsync(stream, isHead, artworkEpisodeId, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (path.StartsWith("/audio/", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(path[7..], out var episodeId))
        {
            await HandleAudioAsync(stream, request, episodeId, cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task HandleArtworkAsync(Stream stream, bool headOnly, long episodeId, CancellationToken cancellationToken)
    {
        var episode = _archive.GetEpisode(episodeId);
        if (episode is null || string.IsNullOrWhiteSpace(episode.ArtworkPath) || !File.Exists(episode.ArtworkPath))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "Artwork is not available.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(episode.ArtworkPath, cancellationToken).ConfigureAwait(false);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, GetImageMime(Path.GetExtension(episode.ArtworkPath)), headOnly, cancellationToken, "Cache-Control: private, max-age=3600\r\n").ConfigureAwait(false);
    }

    private async Task StreamAudioFileAsync(Stream stream, HttpRequest request, string audioPath, string logIdentity, CancellationToken cancellationToken)
    {
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Audio streaming supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var file = new FileInfo(audioPath);
        var length = file.Length;
        if (length <= 0) { await WriteTextResponseAsync(stream, 416, "Range Not Satisfiable", "The recording is empty.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false); return; }
        var lastModifiedUtc = file.LastWriteTimeUtc;
        var etag = $"\"rv-{length:x}-{lastModifiedUtc.Ticks:x}\"";
        var lastModified = lastModifiedUtc.ToString("R", CultureInfo.InvariantCulture);
        long start=0,end=length-1; var partial=false;
        var requestedRange=request.Headers.TryGetValue("range",out var rawRange)?rawRange.Trim():string.Empty;
        var ifRange=request.Headers.TryGetValue("if-range",out var rawIfRange)?rawIfRange.Trim():string.Empty;
        var rangeValidatorMatches=string.IsNullOrEmpty(ifRange) ||
            string.Equals(ifRange,etag,StringComparison.Ordinal) ||
            (DateTimeOffset.TryParse(ifRange,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var ifRangeDate) &&
             lastModifiedUtc <= ifRangeDate.UtcDateTime.AddSeconds(1));
        if (!string.IsNullOrEmpty(requestedRange) && rangeValidatorMatches)
        {
            if (!TryParseRange(requestedRange,length,out start,out end))
            {
                var invalid=$"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{length}\r\nAccept-Ranges: bytes\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(invalid),cancellationToken).ConfigureAwait(false); return;
            }
            partial=true;
        }
        var contentLength=end-start+1; var status=partial?"206 Partial Content":"200 OK";
        var header=new StringBuilder().Append("HTTP/1.1 ").Append(status).Append("\r\nContent-Type: ").Append(GetAudioMime(file.Extension))
            .Append("\r\nContent-Length: ").Append(contentLength)
            .Append("\r\nAccept-Ranges: bytes")
            .Append("\r\nETag: ").Append(etag)
            .Append("\r\nLast-Modified: ").Append(lastModified)
            .Append("\r\nCache-Control: private, max-age=300, no-transform")
            .Append("\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n");
        if(partial) header.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(length).Append("\r\n");
        header.Append("\r\n");
        var userAgent = request.Headers.TryGetValue("user-agent", out var rawUserAgent) ? rawUserAgent : string.Empty;
        _log?.Invoke($"{logIdentity}: {request.Method} range='{requestedRange}', if-range='{ifRange}' => {status}, bytes {start}-{end}/{length}, validator={etag}, agent='{userAgent}'.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()),cancellationToken).ConfigureAwait(false);
        if(request.Method.Equals("HEAD",StringComparison.OrdinalIgnoreCase)) return;
        await using var input=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,256*1024,FileOptions.Asynchronous|FileOptions.RandomAccess);
        input.Seek(start,SeekOrigin.Begin); var buffer=new byte[256*1024]; var remaining=contentLength;
        try { while(remaining>0&&!cancellationToken.IsCancellationRequested){var read=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,remaining)),cancellationToken).ConfigureAwait(false);if(read<=0)break;await stream.WriteAsync(buffer.AsMemory(0,read),cancellationToken).ConfigureAwait(false);remaining-=read;} await stream.FlushAsync(cancellationToken).ConfigureAwait(false);if(remaining>0)_log?.Invoke($"{logIdentity}: response ended {remaining} bytes early for range '{requestedRange}'.");} catch(IOException ex){_log?.Invoke($"{logIdentity}: client disconnected after {contentLength-remaining} of {contentLength} bytes for range '{requestedRange}' ({ex.Message}).");}
    }

    private async Task HandleCanonicalMediaManifestAsync(Stream stream, long episodeId, IReadOnlyDictionary<string, string> query, bool headOnly, CancellationToken cancellationToken)
    {
        query.TryGetValue("recording", out var recordingKey);
        var manifest = _archive.GetCanonicalMediaManifest(episodeId, recordingKey);
        if (manifest is null)
        {
            _log?.Invoke($"Canonical media manifest {episodeId}: no complete plan is currently available for recording '{recordingKey ?? "preferred"}'.");
            await WriteTextResponseAsync(stream, 404, "Not Found", "No complete canonical media manifest is available.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await WriteBytesResponseAsync(stream, 200, "OK", bytes, "application/json; charset=utf-8", headOnly, cancellationToken, "Cache-Control: no-store\r\n").ConfigureAwait(false);
    }

    private async Task HandleCanonicalMediaPartAsync(Stream stream, HttpRequest request, long episodeId, long mediaFileId, IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        query.TryGetValue("recording", out var recordingKey);
        var part = _archive.GetCanonicalMediaPart(episodeId, mediaFileId, recordingKey);
        if (part is null || string.IsNullOrWhiteSpace(part.AudioPath) || !File.Exists(part.AudioPath))
        {
            // A library scan or cloud-backed path can briefly change while the
            // browser moves its decoder. Re-resolve once before returning a 404.
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            part = _archive.GetCanonicalMediaPart(episodeId, mediaFileId, recordingKey);
        }
        if (part is null || string.IsNullOrWhiteSpace(part.AudioPath) || !File.Exists(part.AudioPath))
        {
            var reason = part is null
                ? "the requested part was not in the current canonical plan"
                : string.IsNullOrWhiteSpace(part.AudioPath)
                    ? "the media path was empty"
                    : "the indexed media path was unavailable";
            _log?.Invoke($"Canonical media {episodeId}/{mediaFileId}: 404 because {reason}; recording '{recordingKey ?? "preferred"}'.");
            await WriteTextResponseAsync(stream, 404, "Not Found", "The canonical media part is not available.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        await StreamAudioFileAsync(stream, request, part.AudioPath, $"Canonical media {episodeId}/{mediaFileId}", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCanonicalMediaStartAsync(Stream stream, HttpRequest request, long episodeId, IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        query.TryGetValue("recording", out var recordingKey);
        var requestedPositionMs = query.TryGetValue("positionMs", out var rawPosition) && long.TryParse(rawPosition, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPosition)
            ? Math.Max(0, parsedPosition)
            : 0;
        var manifest = _archive.GetCanonicalMediaManifest(episodeId, recordingKey);
        var part = manifest?.Parts.FirstOrDefault(candidate =>
            requestedPositionMs >= candidate.LogicalStartMs &&
            (requestedPositionMs < candidate.LogicalEndMs || ReferenceEquals(candidate, manifest.Parts[^1])))
            ?? manifest?.Parts.LastOrDefault();
        if (part is null || string.IsNullOrWhiteSpace(part.AudioPath) || !File.Exists(part.AudioPath))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "The canonical starting media is not available.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }
        var localPositionMs = Math.Max(0, requestedPositionMs - part.LogicalStartMs);
        var forcePositioned = query.TryGetValue("positioned", out var positionedValue) &&
            positionedValue.Equals("1", StringComparison.OrdinalIgnoreCase);
        if (forcePositioned || localPositionMs > 0)
        {
            var streamSession = query.TryGetValue("streamSession", out var requestedSession) &&
                !string.IsNullOrWhiteSpace(requestedSession)
                    ? requestedSession.Trim()
                    : $"fallback-{episodeId}-{part.MediaFileId}-{localPositionMs}";
            await StreamPositionedWaveAsync(
                stream,
                request,
                part.AudioPath,
                localPositionMs,
                streamSession,
                $"Canonical positioned media start {episodeId}/{part.MediaFileId}",
                cancellationToken).ConfigureAwait(false);
            return;
        }
        await StreamAudioFileAsync(stream, request, part.AudioPath, $"Canonical media start {episodeId}/{part.MediaFileId}", cancellationToken).ConfigureAwait(false);
    }

    private async Task StreamPositionedWaveAsync(
        Stream stream,
        HttpRequest request,
        string audioPath,
        long positionMs,
        string streamSession,
        string logIdentity,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Positioned web playback requires Windows Media Foundation.");
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            !request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Audio streaming supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        var positioned = GetOrCreatePositionedWaveSession(streamSession, audioPath, positionMs);
        var virtualLength = positioned.VirtualLength;

        long start = 0, end = virtualLength - 1;
        var partial = false;
        var requestedRange = request.Headers.TryGetValue("range", out var rawRange) ? rawRange.Trim() : string.Empty;
        if (!string.IsNullOrWhiteSpace(requestedRange))
        {
            if (!TryParseRange(requestedRange, virtualLength, out start, out end))
            {
                var invalid = $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{virtualLength}\r\nAccept-Ranges: bytes\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(invalid), cancellationToken).ConfigureAwait(false);
                return;
            }
            partial = true;
        }

        var contentLength = end - start + 1;
        var etag = positioned.ETag;
        var status = partial ? "206 Partial Content" : "200 OK";
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status)
            .Append("\r\nContent-Type: audio/wav")
            .Append("\r\nContent-Length: ").Append(contentLength)
            .Append("\r\nAccept-Ranges: bytes")
            .Append("\r\nETag: ").Append(etag)
            .Append("\r\nCache-Control: private, max-age=300, no-transform")
            .Append("\r\nX-Content-Type-Options: nosniff")
            .Append("\r\nConnection: close\r\n");
        if (partial) header.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(virtualLength).Append("\r\n");
        header.Append("\r\n");
        _log?.Invoke($"{logIdentity}: {request.Method} positioned at {positionMs} ms, range='{requestedRange}' => {status}, virtual bytes {start}-{end}/{virtualLength}.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), cancellationToken).ConfigureAwait(false);
        if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var written = await positioned.WriteRangeAsync(stream, start, contentLength, cancellationToken).ConfigureAwait(false);
            if (written < contentLength)
                _log?.Invoke($"{logIdentity}: positioned response ended {contentLength - written} bytes early for range '{requestedRange}'.");
        }
        catch (IOException ex)
        {
            _log?.Invoke($"{logIdentity}: positioned client disconnected during range '{requestedRange}' ({ex.Message}).");
        }
    }

    private PositionedWaveSession GetOrCreatePositionedWaveSession(
        string streamSession,
        string audioPath,
        long positionMs)
    {
        var identityBytes = Encoding.UTF8.GetBytes($"{streamSession}\n{audioPath}\n{positionMs}");
        var key = Convert.ToHexString(SHA256.HashData(identityBytes));
        var now = DateTimeOffset.UtcNow;
        lock (_positionedWaveSessionsGate)
        {
            foreach (var stale in _positionedWaveSessions
                         .Where(pair => now - pair.Value.LastAccessUtc >= PositionedWaveSessionIdleLifetime)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                if (_positionedWaveSessions[stale].TryDispose())
                    _positionedWaveSessions.Remove(stale);
            }

            if (_positionedWaveSessions.TryGetValue(key, out var existing))
            {
                if (existing.MatchesCurrentFile()) return existing;
                if (!existing.TryDispose()) return existing;
                _positionedWaveSessions.Remove(key);
            }

            if (_positionedWaveSessions.Count >= PositionedWaveSessionSoftLimit)
            {
                foreach (var oldest in _positionedWaveSessions
                             .OrderBy(pair => pair.Value.LastAccessUtc)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    if (!_positionedWaveSessions[oldest].TryDispose()) continue;
                    _positionedWaveSessions.Remove(oldest);
                    if (_positionedWaveSessions.Count < PositionedWaveSessionSoftLimit) break;
                }
            }

            var created = new PositionedWaveSession(audioPath, positionMs);
            _positionedWaveSessions[key] = created;
            return created;
        }
    }

    private void DisposePositionedWaveSessions()
    {
        PositionedWaveSession[] sessions;
        lock (_positionedWaveSessionsGate)
        {
            sessions = _positionedWaveSessions.Values.ToArray();
            _positionedWaveSessions.Clear();
        }
        foreach (var session in sessions) session.Dispose();
    }

    private sealed class PositionedWaveSession : IDisposable
    {
        private readonly string _audioPath;
        private readonly long _fileLength;
        private readonly DateTime _lastWriteTimeUtc;
        private readonly TimeSpan _requestedTime;
        private readonly SemaphoreSlim _access = new(1, 1);
        private WaveStream _reader;
        private long _decodedStart;
        private long _dataCursor;
        private bool _disposed;

        public PositionedWaveSession(string audioPath, long positionMs)
        {
            _audioPath = audioPath;
            var file = new FileInfo(audioPath);
            _fileLength = file.Length;
            _lastWriteTimeUtc = file.LastWriteTimeUtc;
            _reader = OpenPositionedAudioReader(audioPath);
            WaveFormat = _reader.WaveFormat;
            if (WaveFormat.Encoding != WaveFormatEncoding.Pcm || WaveFormat.BitsPerSample is not (8 or 16 or 24 or 32))
                throw new InvalidDataException($"The decoded format {WaveFormat.Encoding}/{WaveFormat.BitsPerSample}-bit cannot be represented as a standard PCM wave stream.");

            _requestedTime = TimeSpan.FromMilliseconds(Math.Clamp(
                positionMs,
                0,
                Math.Max(0, _reader.TotalTime.TotalMilliseconds)));
            PositionReaderAtStart();
            var availableData = Math.Max(0, _reader.Length - _decodedStart);
            availableData -= availableData % BlockAlign;
            DataLength = Math.Min(availableData, (long)uint.MaxValue - 64);
            DataLength -= DataLength % BlockAlign;
            WaveHeader = CreatePcmWaveHeader(WaveFormat, DataLength);
            VirtualLength = WaveHeader.LongLength + DataLength;
            ETag = $"\"rv-positioned-{_fileLength:x}-{_lastWriteTimeUtc.Ticks:x}-{positionMs:x}\"";
            LastAccessUtc = DateTimeOffset.UtcNow;
        }

        public WaveFormat WaveFormat { get; }
        public int BlockAlign => Math.Max(1, WaveFormat.BlockAlign);
        public long DataLength { get; }
        public byte[] WaveHeader { get; }
        public long VirtualLength { get; }
        public string ETag { get; }
        public DateTimeOffset LastAccessUtc { get; private set; }

        public bool MatchesCurrentFile()
        {
            var file = new FileInfo(_audioPath);
            return file.Exists && file.Length == _fileLength && file.LastWriteTimeUtc == _lastWriteTimeUtc;
        }

        public async Task<long> WriteRangeAsync(
            Stream output,
            long virtualStart,
            long count,
            CancellationToken cancellationToken)
        {
            await _access.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                LastAccessUtc = DateTimeOffset.UtcNow;
                var remaining = Math.Max(0, count);
                var cursor = Math.Max(0, virtualStart);
                long written = 0;
                if (cursor < WaveHeader.LongLength)
                {
                    var headerCount = (int)Math.Min(remaining, WaveHeader.LongLength - cursor);
                    await output.WriteAsync(WaveHeader.AsMemory((int)cursor, headerCount), cancellationToken).ConfigureAwait(false);
                    cursor += headerCount;
                    remaining -= headerCount;
                    written += headerCount;
                }
                if (remaining <= 0) return written;

                var targetDataOffset = Math.Max(0, cursor - WaveHeader.LongLength);
                if (targetDataOffset < _dataCursor) PositionReaderAtStart();
                var buffer = new byte[256 * 1024];
                while (_dataCursor < targetDataOffset)
                {
                    var discardCount = (int)Math.Min(buffer.Length, targetDataOffset - _dataCursor);
                    var discarded = _reader.Read(buffer, 0, discardCount);
                    if (discarded <= 0) return written;
                    _dataCursor += discarded;
                }

                while (remaining > 0 && !cancellationToken.IsCancellationRequested)
                {
                    var read = _reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    _dataCursor += read;
                    remaining -= read;
                    written += read;
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                LastAccessUtc = DateTimeOffset.UtcNow;
                return written;
            }
            finally
            {
                _access.Release();
            }
        }

        private void PositionReaderAtStart()
        {
            _reader.CurrentTime = _requestedTime;
            _decodedStart = _reader.Position - _reader.Position % BlockAlign;
            _dataCursor = 0;
        }

        public bool TryDispose()
        {
            if (!_access.Wait(0)) return false;
            try
            {
                DisposeCore();
                return true;
            }
            finally
            {
                _access.Release();
            }
        }

        public void Dispose()
        {
            _access.Wait();
            try { DisposeCore(); }
            finally
            {
                _access.Release();
                _access.Dispose();
            }
        }

        private void DisposeCore()
        {
            if (_disposed) return;
            _disposed = true;
            _reader.Dispose();
        }
    }

    private static WaveStream OpenPositionedAudioReader(string audioPath)
    {
        if (Path.GetExtension(audioPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return new WaveFileReader(audioPath);
        return new MediaFoundationReader(
            audioPath,
            new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = false });
    }

    private static byte[] CreatePcmWaveHeader(WaveFormat format, long dataLength)
    {
        using var output = new MemoryStream(44);
        using (var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write((uint)(36 + dataLength));
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16u);
            writer.Write((ushort)1);
            writer.Write((ushort)format.Channels);
            writer.Write((uint)format.SampleRate);
            writer.Write((uint)format.AverageBytesPerSecond);
            writer.Write((ushort)format.BlockAlign);
            writer.Write((ushort)format.BitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write((uint)dataLength);
        }
        return output.ToArray();
    }

    private async Task HandleAudioAsync(Stream stream, HttpRequest request, long episodeId, CancellationToken cancellationToken)
    {
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTextResponseAsync(stream, 405, "Method Not Allowed", "Audio streaming supports GET and HEAD only.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        var episode = _archive.GetEpisode(episodeId);
        if (episode is null || string.IsNullOrWhiteSpace(episode.AudioPath) || !File.Exists(episode.AudioPath))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "The recording is not currently available on this computer.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        var file = new FileInfo(episode.AudioPath);
        var length = file.Length;
        if (length <= 0)
        {
            await WriteTextResponseAsync(stream, 416, "Range Not Satisfiable", "The recording is empty.", "text/plain; charset=utf-8", cancellationToken).ConfigureAwait(false);
            return;
        }

        long start = 0;
        long end = length - 1;
        var partial = false;
        var requestedRange = request.Headers.TryGetValue("range", out var rawRange) ? rawRange.Trim() : string.Empty;
        if (!string.IsNullOrEmpty(requestedRange))
        {
            if (!TryParseRange(requestedRange, length, out start, out end))
            {
                var invalidHeader = new StringBuilder()
                    .Append("HTTP/1.1 416 Range Not Satisfiable\r\n")
                    .Append("Content-Range: bytes */").Append(length).Append("\r\n")
                    .Append("Accept-Ranges: bytes\r\n")
                    .Append("Content-Length: 0\r\n")
                    .Append("Cache-Control: no-store, no-cache, must-revalidate\r\n")
                    .Append("Pragma: no-cache\r\n")
                    .Append("Expires: 0\r\n")
                    .Append("Vary: Range\r\n")
                    .Append("Connection: close\r\n\r\n");
                _log?.Invoke($"Audio {episodeId}: rejected range '{requestedRange}' for {length} bytes.");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(invalidHeader.ToString()), cancellationToken).ConfigureAwait(false);
                return;
            }
            partial = true;
        }

        var contentLength = end - start + 1;
        var mime = GetAudioMime(file.Extension);
        var status = partial ? "206 Partial Content" : "200 OK";
        var safeName = Uri.EscapeDataString(file.Name);
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append("\r\n")
            .Append("Content-Type: ").Append(mime).Append("\r\n")
            .Append("Content-Length: ").Append(contentLength).Append("\r\n")
            .Append("Accept-Ranges: bytes\r\n")
            .Append("Content-Encoding: identity\r\n")
            .Append("Content-Disposition: inline; filename*=UTF-8''").Append(safeName).Append("\r\n")
            .Append("Cache-Control: no-store, no-cache, must-revalidate\r\n")
            .Append("Pragma: no-cache\r\n")
            .Append("Expires: 0\r\n")
            .Append("Vary: Range\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n");
        if (partial) header.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(length).Append("\r\n");
        header.Append("\r\n");

        var userAgent = request.Headers.TryGetValue("user-agent", out var rawUserAgent) ? rawUserAgent : string.Empty;
        _log?.Invoke($"Audio {episodeId}: {request.Method} range='{requestedRange}' => {status}, bytes {start}-{end}/{length}, length {contentLength}, agent='{userAgent}'.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), cancellationToken).ConfigureAwait(false);
        if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return;

        await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 256 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        input.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[256 * 1024];
        var remaining = contentLength;
        try
        {
            while (remaining > 0 && !cancellationToken.IsCancellationRequested)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (remaining > 0)
                _log?.Invoke($"Audio {episodeId}: response ended {remaining} bytes early for requested range '{requestedRange}'.");
        }
        catch (IOException ex)
        {
            _log?.Invoke($"Audio {episodeId}: client disconnected during range '{requestedRange}' after {contentLength - remaining} of {contentLength} bytes ({ex.Message}).");
        }
    }

    private static bool TryMatchCanonicalMediaManifest(string path, out long episodeId)
    {
        episodeId = 0;
        var suffix = "/media-manifest";
        if (!path.StartsWith(WebApiRoutes.Broadcasts + "/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var value = path[(WebApiRoutes.Broadcasts.Length + 1)..^suffix.Length];
        return long.TryParse(value, out episodeId);
    }

    private static bool TryMatchCanonicalMediaPart(string path, out long episodeId, out long mediaFileId)
    {
        episodeId = 0; mediaFileId = 0;
        var prefix = WebApiRoutes.Broadcasts + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return rest.Length == 3 && rest[1].Equals("media", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(rest[0], out episodeId) && long.TryParse(rest[2], out mediaFileId);
    }

    private static bool TryMatchCanonicalMediaStart(string path, out long episodeId)
    {
        episodeId = 0;
        var suffix = "/media-start";
        if (!path.StartsWith(WebApiRoutes.Broadcasts + "/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var value = path[(WebApiRoutes.Broadcasts.Length + 1)..^suffix.Length];
        return long.TryParse(value, out episodeId);
    }

}
