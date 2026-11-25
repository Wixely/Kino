using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kino.Media;
using Kino.Media.FFmpeg;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kino.Platform.Windows;

public sealed record AudioDevice(string Id, string Name);

public static class AudioDevices
{
    public static IReadOnlyList<AudioDevice> Microphones()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(device =>
                {
                    using (device)
                    {
                        return new AudioDevice(device.ID, device.FriendlyName);
                    }
                })
                .ToArray();
        }
        catch (COMException)
        {
            return [];
        }
    }
}

/// <summary>Writes one WASAPI source to a WAV file. Gaps in delivery are filled with silence against
/// the shared recording clock: loopback capture delivers nothing while no application plays sound, and
/// without filling those gaps the track would drift ahead of the video.</summary>
internal sealed class AudioTrackRecorder : IDisposable
{
    private static readonly long FlushInterval = Stopwatch.Frequency * 2;
    private readonly IWaveIn _capture;
    private readonly WaveFileWriter _writer;
    private readonly object _sync = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _blockAlign;
    private readonly int _sampleRate;
    private readonly Func<long> _clock;
    private long _framesWritten;
    private long _lastFlush;
    private bool _closed;

    public AudioTrackRecorder(IWaveIn capture, string path, Func<long>? clock = null)
    {
        _capture = capture;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _writer = new WaveFileWriter(path, capture.WaveFormat);
        _blockAlign = capture.WaveFormat.BlockAlign;
        _sampleRate = capture.WaveFormat.SampleRate;
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, args) =>
        {
            Lost = args.Exception?.Message;
            _stopped.TrySetResult();
        };
    }

    /// <summary>The stopwatch timestamp that sample zero corresponds to.</summary>
    public long Origin { get; private set; }

    public string? Lost { get; private set; }

    public void Start()
    {
        Origin = _clock();
        _lastFlush = Origin;
        _capture.StartRecording();
    }

    /// <summary>Stops capture, pads to <paramref name="endTimestamp"/> and closes the file. Returns
    /// the track duration in seconds.</summary>
    public async Task<double> StopAsync(long endTimestamp)
    {
        _capture.StopRecording();
        await Task.WhenAny(_stopped.Task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        lock (_sync)
        {
            if (!_closed)
            {
                PadTo(FramesAt(endTimestamp));
                _writer.Dispose();
                _closed = true;
            }

            return _framesWritten / (double)_sampleRate;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (!_closed)
            {
                _writer.Dispose();
                _closed = true;
            }
        }

        _capture.Dispose();
    }

    private long FramesAt(long timestamp) => (long)((timestamp - Origin) / (double)Stopwatch.Frequency * _sampleRate);

    private void OnData(object? sender, WaveInEventArgs args)
    {
        var now = _clock();
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            var packetFrames = args.BytesRecorded / _blockAlign;
            // The packet ends now, so it started packetFrames ago. Anything missing before it is a gap.
            // A tolerance of 30 ms keeps ordinary delivery jitter from inserting silence.
            var packetStart = FramesAt(now) - packetFrames;
            if (packetStart - _framesWritten > _sampleRate * 3 / 100)
            {
                PadTo(packetStart);
            }

            _writer.Write(args.Buffer, 0, args.BytesRecorded);
            _framesWritten += packetFrames;
            if (now - _lastFlush > FlushInterval)
            {
                // Flush rewrites the WAV header sizes, so the file stays readable after a crash.
                _writer.Flush();
                _lastFlush = now;
            }
        }
    }

    private void PadTo(long frames)
    {
        var missing = frames - _framesWritten;
        if (missing <= 0)
        {
            return;
        }

        var silence = new byte[Math.Min(missing, _sampleRate) * _blockAlign];
        while (missing > 0)
        {
            var chunk = Math.Min(missing, silence.Length / _blockAlign);
            _writer.Write(silence, 0, (int)(chunk * _blockAlign));
            missing -= chunk;
            _framesWritten += chunk;
        }
    }
}

/// <summary>Records a screen region, plus system and microphone audio as separate WAV files. The JPEG
/// modes capture with Desktop Duplication into a Kino frame stream; the lossless mode captures with GDI
/// into system ffmpeg.</summary>
public sealed class WindowsScreenRecorder : IScreenRecorder
{
    private readonly Func<FfmpegTools?> _tools;
    private readonly Func<string?> _unavailable;
    private readonly Func<KinoSettings> _settings;

    /// <param name="tools">The ffmpeg found for the lossless mode, or null.</param>
    /// <param name="unavailable">Why ffmpeg cannot record, or null.</param>
    /// <param name="settings">The current settings, which choose the recording mode.</param>
    public WindowsScreenRecorder(Func<FfmpegTools?> tools, Func<string?> unavailable, Func<KinoSettings> settings)
    {
        _tools = tools;
        _unavailable = unavailable;
        _settings = settings;
    }

    /// <summary>Only the lossless mode needs ffmpeg while recording.</summary>
    public string? Unavailable => _settings().RecordingMode == RecordingMode.Lossless ? _unavailable() : null;

    public async Task<IActiveRecording> StartAsync(PixelRect region, string sessionDirectory, RecordingManifest manifest, KinoSettings settings)
    {
        IVideoPipeline video;
        manifest.Mode = settings.RecordingMode;
        if (settings.RecordingMode == RecordingMode.Lossless)
        {
            var tools = _tools() ?? throw new InvalidOperationException(_unavailable() ?? "ffmpeg is not available.");
            manifest.VideoFormat = StagingFormat.LosslessMatroska;
            video = new LosslessPipeline(region, settings.IncludeCursor,
                new StagingEncoder(tools, region.Width, region.Height, manifest.FrameRate, Path.Combine(sessionDirectory, manifest.VideoFileName)));
        }
        else
        {
            manifest.VideoFormat = StagingFormat.JpegFrames;
            manifest.VideoFileName = FrameFile.FileName;
            video = new JpegPipeline(region, settings.IncludeCursor, new FrameStreamWriter(
                Path.Combine(sessionDirectory, manifest.VideoFileName), region.Width, region.Height, manifest.FrameRate,
                settings.RecordingMode == RecordingMode.Jpeg ? FrameStreamMode.Full : FrameStreamMode.Delta, settings.JpegQuality));
        }

        var audio = new List<(RecordedAudioTrack Track, AudioTrackRecorder Recorder)>();
        if (settings.RecordSystemAudio)
        {
            AddTrack(manifest, audio, sessionDirectory, "system", AudioSourceKind.System, "System audio", () => new WasapiLoopbackCapture());
        }

        if (settings.RecordMicrophone)
        {
            AddTrack(manifest, audio, sessionDirectory, "microphone", AudioSourceKind.Microphone, "Microphone", () => OpenMicrophone(settings.MicrophoneDeviceId));
        }

        var follower = settings.FollowCursor && Native.GetCursorPos(out var pointer)
            ? new CursorFollower(region, Native.VirtualScreen(), new PixelPoint(pointer.X, pointer.Y))
            : null;
        var active = new ActiveRecording(manifest, video, audio, region, follower);
        try
        {
            foreach (var (track, recorder) in audio)
            {
                try
                {
                    recorder.Start();
                }
                catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or InvalidOperationException)
                {
                    track.Problem = Describe(exception, track.Kind);
                }
            }

            await active.StartVideoAsync().ConfigureAwait(false);
            return active;
        }
        catch
        {
            await active.AbortAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void AddTrack(RecordingManifest manifest, List<(RecordedAudioTrack, AudioTrackRecorder)> audio, string sessionDirectory,
        string id, AudioSourceKind kind, string label, Func<IWaveIn> open)
    {
        var track = new RecordedAudioTrack { Id = id, Kind = kind, Label = label, FileName = id + ".wav" };
        manifest.AudioTracks.Add(track);
        try
        {
            var capture = open();
            track = manifest.AudioTracks[^1];
            audio.Add((track, new AudioTrackRecorder(capture, Path.Combine(sessionDirectory, track.FileName))));
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            track.Problem = Describe(exception, kind);
        }
    }

    private static WasapiCapture OpenMicrophone(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        if (!string.IsNullOrEmpty(deviceId))
        {
            device = enumerator.GetDevice(deviceId);
        }
        else if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        else
        {
            throw new InvalidOperationException("No microphone is connected.");
        }

        return new WasapiCapture(device);
    }

    private static string Describe(Exception exception, AudioSourceKind kind) => exception switch
    {
        UnauthorizedAccessException => "Windows denied access. Check Settings > Privacy & security > Microphone.",
        COMException { HResult: unchecked((int)0x80070005) } => "Windows denied access. Check Settings > Privacy & security > Microphone.",
        COMException { HResult: unchecked((int)0x80070490) } => "The selected device is not connected.",
        _ when kind == AudioSourceKind.System => $"System audio could not be captured: {exception.Message}",
        _ => exception.Message,
    };

    private sealed class ActiveRecording : IActiveRecording
    {
        private readonly RecordingManifest _manifest;
        private readonly IVideoPipeline _video;
        private readonly List<(RecordedAudioTrack Track, AudioTrackRecorder Recorder)> _audio;
        private readonly CursorFollower? _follower;
        private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stopwatch _elapsed = new();
        private Thread? _thread;
        private volatile bool _stopRequested;
        private PixelRect _region;
        private long _lastCapture;
        private long _videoOrigin;
        private long _frames;
        private long _repeated;
        private string? _failure;

        public ActiveRecording(RecordingManifest manifest, IVideoPipeline video, List<(RecordedAudioTrack, AudioTrackRecorder)> audio,
            PixelRect region, CursorFollower? follower)
        {
            _manifest = manifest;
            _video = video;
            _audio = audio;
            _region = region;
            _follower = follower;
        }

        public TimeSpan Elapsed => _elapsed.Elapsed;

        public event Action<PixelRect>? RegionMoved;

        public Task StartVideoAsync()
        {
            _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Kino video capture", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            return _firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async Task StopAsync()
        {
            _stopRequested = true;
            _thread?.Join();
            var end = _videoOrigin + (long)(_frames / (double)_manifest.FrameRate * Stopwatch.Frequency);
            _elapsed.Stop();
            string? videoFailure = null;
            try
            {
                await _video.CompleteAsync().ConfigureAwait(false);
            }
            catch (FfmpegException exception)
            {
                videoFailure = $"{exception.Message} {exception.Diagnostics}".Trim();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                videoFailure = $"The temporary video could not be completed: {exception.Message}";
            }
            finally
            {
                _video.Dispose();
            }

            foreach (var (track, recorder) in _audio)
            {
                if (track.Problem is not null)
                {
                    recorder.Dispose();
                    continue;
                }

                track.DurationSeconds = await recorder.StopAsync(end).ConfigureAwait(false);
                track.OffsetSeconds = (recorder.Origin - _videoOrigin) / (double)Stopwatch.Frequency;
                if (recorder.Lost is { } lost)
                {
                    track.Warning = $"The device stopped part-way through: {lost}";
                }

                recorder.Dispose();
            }

            _manifest.FramesWritten = _frames;
            _manifest.FramesRepeated = _repeated + _video.Dropped;
            _manifest.CaptureDetails = _follower is null ? _video.Details : $"{_video.Details}; the region followed the pointer";
            _manifest.DurationSeconds = _frames / (double)_manifest.FrameRate;
            _manifest.LastError = _failure ?? videoFailure;
        }

        public async Task AbortAsync()
        {
            _stopRequested = true;
            _thread?.Join();
            _video.Dispose();
            foreach (var (_, recorder) in _audio)
            {
                try
                {
                    await recorder.StopAsync(Stopwatch.GetTimestamp()).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is COMException or InvalidOperationException)
                {
                    // Already failing; the session is discarded.
                }

                recorder.Dispose();
            }
        }

        // Frames are taken on a fixed clock: frame n belongs at origin + n / fps. When capture or storage
        // falls behind, the missed slots repeat the latest image, so the video keeps wall-clock duration
        // and stays aligned with the audio.
        private void CaptureLoop()
        {
            _ = Native.timeBeginPeriod(1);
            try
            {
                var interval = Stopwatch.Frequency / (double)_manifest.FrameRate;
                while (!_stopRequested)
                {
                    if (_frames > 0)
                    {
                        var due = _videoOrigin + (long)(_frames * interval);
                        var wait = (due - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                        if (wait > 1)
                        {
                            Thread.Sleep((int)wait - 1);
                        }

                        while (Stopwatch.GetTimestamp() < due)
                        {
                            Thread.SpinWait(50);
                        }
                    }

                    _video.Capture(_frames, NextRegion());
                    if (_frames == 0)
                    {
                        _videoOrigin = Stopwatch.GetTimestamp();
                        _elapsed.Start();
                        _firstFrame.TrySetResult();
                    }

                    _frames++;
                    while (!_stopRequested && Stopwatch.GetTimestamp() >= _videoOrigin + (long)((_frames + 1) * interval))
                    {
                        _video.Repeat(_frames);
                        _frames++;
                        _repeated++;
                    }
                }
            }
            catch (Exception exception) when (exception is FfmpegException or InvalidOperationException or IOException or InvalidDataException)
            {
                _failure = $"Video capture stopped early: {exception.Message}";
                _firstFrame.TrySetException(exception);
            }
            finally
            {
                _ = Native.timeEndPeriod(1);
            }
        }

        /// <summary>The region for the frame about to be captured: fixed, or moved towards the
        /// pointer by the time since the previous capture.</summary>
        private PixelRect NextRegion()
        {
            var now = Stopwatch.GetTimestamp();
            if (_follower is not null && _lastCapture != 0 && Native.GetCursorPos(out var pointer))
            {
                var region = _follower.Next(new PixelPoint(pointer.X, pointer.Y), Stopwatch.GetElapsedTime(_lastCapture, now));
                if (region != _region)
                {
                    _region = region;
                    RegionMoved?.Invoke(region);
                }
            }

            _lastCapture = now;
            return _region;
        }
    }

    /// <summary>Captures and stores video frames for one recording, called from the capture thread.</summary>
    private interface IVideoPipeline : IDisposable
    {
        /// <summary>Frames captured but not stored because storage fell behind; they play as repeats.</summary>
        long Dropped { get; }

        /// <summary>How frames were captured and stored, for diagnostics.</summary>
        string Details { get; }

        /// <summary>Captures and stores the frame for <paramref name="slot"/> from
        /// <paramref name="region"/>, which keeps the recording's size but may move between frames.</summary>
        void Capture(long slot, PixelRect region);

        /// <summary>Stores a repeat of the previous frame for a slot that capture missed.</summary>
        void Repeat(long slot);

        /// <summary>Finishes the temporary video after the last frame.</summary>
        Task CompleteAsync();
    }

    /// <summary>GDI frames piped to ffmpeg as lossless H.264, every slot written.</summary>
    private sealed class LosslessPipeline : IVideoPipeline
    {
        private readonly bool _includeCursor;
        private readonly StagingEncoder _encoder;
        private readonly GdiFrameGrabber _grabber;
        private byte[]? _last;

        public LosslessPipeline(PixelRect region, bool includeCursor, StagingEncoder encoder)
        {
            _includeCursor = includeCursor;
            _encoder = encoder;
            _grabber = new GdiFrameGrabber(region.Width, region.Height);
        }

        public long Dropped => 0;

        public string Details => "GDI capture, lossless H.264 through ffmpeg";

        public void Capture(long slot, PixelRect region)
        {
            ReadOnlySpan<byte> frame;
            try
            {
                frame = _grabber.Grab(region.X, region.Y, _includeCursor);
            }
            catch (InvalidOperationException) when (_last is not null)
            {
                // The secure desktop (UAC, lock screen) cannot be copied; hold the last image.
                frame = _last;
            }

            _encoder.WriteFrame(frame);
            if (_last is null || frame != _last.AsSpan())
            {
                _last ??= new byte[frame.Length];
                frame.CopyTo(_last);
            }
        }

        public void Repeat(long slot) => _encoder.WriteFrame(_last);

        public Task CompleteAsync() => _encoder.CompleteAsync(TimeSpan.FromSeconds(60));

        public void Dispose()
        {
            _encoder.Dispose();
            _grabber.Dispose();
        }
    }

    /// <summary>
    /// Desktop Duplication frames stored as a JPEG frame stream. Capture stays on the frame clock while a
    /// separate thread compares and encodes; when that thread falls behind, captured frames are dropped
    /// and play back as repeats. Repeated slots store nothing.
    /// </summary>
    private sealed class JpegPipeline : IVideoPipeline
    {
        private readonly RegionFrameSource _source;
        private readonly FrameStreamWriter _writer;
        private readonly BlockingCollection<(long Slot, RawFrame Frame, IReadOnlyList<PixelRect>? Damage)> _queue = new(boundedCapacity: 3);
        private readonly Thread _thread;
        private volatile Exception? _failure;
        private bool _damageLost;

        public JpegPipeline(PixelRect region, bool includeCursor, FrameStreamWriter writer)
        {
            _source = new RegionFrameSource(region, includeCursor);
            _writer = writer;
            _thread = new Thread(Store) { IsBackground = true, Name = "Kino frame storage" };
            _thread.Start();
        }

        public long Dropped { get; private set; }

        public string Details => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{_source.Backend} capture ({_source.GdiFrames} frames through GDI); {_writer.FullFrames} full JPEG frames, {_writer.PatchFrames} patches, {_writer.UnchangedFrames} unchanged, {Dropped} dropped; {_writer.Bytes / 1024} KB");

        public void Capture(long slot, PixelRect region)
        {
            if (_failure is { } failure)
            {
                throw new IOException($"The temporary video could not be written: {failure.Message}", failure);
            }

            var (frame, damage) = _source.Next(region);
            // A dropped frame's changes are missing from the next frame's report, so that report cannot
            // be trusted.
            if (!_queue.TryAdd((slot, frame, _damageLost ? null : damage)))
            {
                Dropped++;
                _damageLost = true;
                return;
            }

            _damageLost = false;
        }

        public void Repeat(long slot)
        {
        }

        public Task CompleteAsync()
        {
            Finish();
            return _failure is { } failure
                ? Task.FromException(new IOException($"The temporary video could not be written: {failure.Message}", failure))
                : Task.CompletedTask;
        }

        public void Dispose()
        {
            Finish();
            _source.Dispose();
            _queue.Dispose();
        }

        private void Finish()
        {
            if (!_queue.IsAddingCompleted)
            {
                _queue.CompleteAdding();
            }

            _thread.Join();
            _writer.Dispose();
        }

        private void Store()
        {
            try
            {
                foreach (var (slot, frame, damage) in _queue.GetConsumingEnumerable())
                {
                    _writer.Write(slot, frame, damage);
                }

                _writer.Complete();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _failure = exception;
                // Keeps the capture thread from blocking; it reports the failure on its next frame.
                foreach (var _ in _queue.GetConsumingEnumerable())
                {
                }
            }
        }
    }
}
