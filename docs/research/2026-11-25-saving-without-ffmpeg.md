# Research: Saving videos without FFmpeg (Milestone 3)

- Status: Parked
- Date: 2026-11-25
- Owner: Wixely
- Related: [Recording and rendering decision](../decisions/2026-11-24-recording-and-rendering.md), [plan](../plan.md) milestone M3

These are spike notes, not a decision. Nothing here is implemented in Kino. JPEG recording modes already record without FFmpeg; only saving the final video still needs it.

## Summary

Windows Media Foundation is the practical way to save MP4 without FFmpeg on Windows. H264Sharp produces correct output but is about 300 times too slow for screen recordings in its current form; it remains the candidate for a pure-managed Linux fallback after substantial performance work.

## Measurements

All runs used a 2-second, 60-frame, 1920×1080, 30 fps synthetic clip on the development machine (Windows 10 22H2).

| Encoder | Content | Time | Speed | Size |
|---|---|---|---|---|
| H264Sharp, CAVLC | still | 132.9 s | 0.5 fps | 76 KB |
| H264Sharp, CABAC | still | 132.1 s | 0.5 fps | 65 KB |
| H264Sharp, CAVLC | moving | 142.7 s | 0.4 fps | 3,205 KB |
| H264Sharp, CABAC | moving | 146.5 s | 0.4 fps | 2,829 KB |
| Media Foundation, software | moving | 0.38 s | 157 fps | 2,573 KB |
| Media Foundation, hardware allowed | moving | 0.35 s | 171 fps | 2,255 KB |

At these speeds a 10-minute recording takes about 10 hours with H264Sharp and about 2 minutes with Media Foundation.

## H264Sharp

- MIT, pure C#, the owner's own package. FFmpeg decodes every frame it produces (Constrained Baseline).
- About 2.2 s per 1080p frame regardless of content, so time is spent on per-block work (motion search, allocations) rather than on changes.
- Limits found:
  - batch API only, with no frame-by-frame input;
  - only the first frame is an IDR frame, so long videos cannot be seeked;
  - fixed QP, deblocking disabled;
  - single-threaded, no SIMD;
  - raw Annex-B output only, with no MP4/MKV muxer and no audio.
- Work needed to be viable, roughly in order of payoff:
  - early P_Skip for unchanged macroblocks, which makes static screen content nearly free;
  - a streaming API and periodic IDR frames;
  - multiple threads (slices or frame rows), SIMD transforms and SAD, allocation removal;
  - a container muxer and an audio encoder, or pairing with another managed audio path.
- Moving content needs roughly a 60× speed-up to reach real time at 1080p30.

## Windows Media Foundation

- Accessed through `Vortice.MediaFoundation` 3.8.3 (MIT), the same library family as Kino's existing `Vortice.Direct3D11` dependency.
- The `IMFSinkWriter` path wrote one MP4 with:
  - H.264 video (Constrained Baseline by default, 8 Mbit/s requested);
  - two separate AAC LC 48 kHz stereo tracks, in the order they were added, with durations matching the video.
- RGB32 (top-down BGRA) input with `MF_MT_DEFAULT_STRIDE = width * 4` gave correct orientation and colours. Mean difference against the source frame was about 7.7 per channel, against 14.8 when flipped and 167 with red and blue swapped.
- Enabling hardware transforms helped only slightly on this machine.

### Gaps

| Area | Finding |
|---|---|
| MP4 | Fully covered, including ordered multi-track audio. |
| MKV | Media Foundation encodes but has no Matroska sink. Needs a small managed Matroska muxer around MF-encoded H.264 and AAC samples. |
| WebM | Not possible: Windows ships no VP9 or Opus encoder. Must stay FFmpeg-only and be stated before saving. |
| Text quality | A fixed bitrate is unsuitable for screen text. Needs quality-based VBR and High profile (requested explicitly), checked against real text content. |
| Track names | The sink writer does not write track titles such as "System audio" and "Microphone". Patching the MP4 `udta`/`hdlr` names afterwards would fix it. |
| Availability | Missing on Windows N editions without the Media Feature Pack and possibly on Windows Server. Not checked on GitHub's `windows-latest` runner; tests would need to skip when it is missing. Kino would need to detect it and explain. |

## Recommendation when resumed

1. Add a Media Foundation render backend for MP4, used automatically when FFmpeg is not found and selectable in Settings (Auto / FFmpeg / Windows).
2. Add MKV through a small built-in Matroska muxer.
3. Keep WebM FFmpeg-only, shown as a stated limit rather than a silent format change.
4. Revisit H264Sharp for Linux (M5) once it has frame-by-frame input and the performance work above.

Open questions for the owner:

- Build the Media Foundation backend, MP4 first?
- Is "WebM needs FFmpeg" acceptable as the stated limit for the reduced mode?

## Appendix: Media Foundation spike

The spike was a standalone console app (`net10.0`, `AllowUnsafeBlocks`, package `Vortice.MediaFoundation` 3.8.3). It reads raw BGRA frames from a file and writes synthetic sine-tone audio. Arguments: `frames.bgra width height frames output.mp4 [hw]`.

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

var (input, width, height, frames, output) = (args[0], int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]), args[4]);
var hardware = args.Length > 5 && args[5] == "hw";
const int Fps = 30;
const int SampleRate = 48000;
var h264 = new Guid("34363248-0000-0010-8000-00AA00389B71");

MediaFactory.MFStartup();
using var attributes = MediaFactory.MFCreateAttributes(2);
attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, hardware ? 1u : 0u);
attributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);
File.Delete(output);
using var writer = MediaFactory.MFCreateSinkWriterFromURL(output, null, attributes);

// Video: H.264 out, RGB32 (top-down BGRA) in.
using var videoOut = MediaFactory.MFCreateMediaType();
videoOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
videoOut.Set(MediaTypeAttributeKeys.Subtype, h264);
videoOut.Set(MediaTypeAttributeKeys.AvgBitrate, 8_000_000u);
videoOut.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
videoOut.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | (uint)height);
videoOut.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)Fps << 32) | 1u);
videoOut.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1u);
var video = writer.AddStream(videoOut);
using var videoIn = MediaFactory.MFCreateMediaType();
videoIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
videoIn.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
videoIn.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
videoIn.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | (uint)height);
videoIn.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)Fps << 32) | 1u);
videoIn.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1u);
videoIn.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4));
writer.SetInputMediaType(video, videoIn, null);

// Two audio tracks: AAC out, 16-bit stereo PCM in.
int AddAudio()
{
    using var aacOut = MediaFactory.MFCreateMediaType();
    aacOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
    aacOut.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
    aacOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)SampleRate);
    aacOut.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
    aacOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
    aacOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000u);
    var index = writer.AddStream(aacOut);
    using var pcmIn = MediaFactory.MFCreateMediaType();
    pcmIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
    pcmIn.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
    pcmIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)SampleRate);
    pcmIn.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
    pcmIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
    pcmIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u);
    pcmIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(SampleRate * 4));
    writer.SetInputMediaType(index, pcmIn, null);
    return index;
}

var tracks = new[] { (Index: AddAudio(), Hz: 440.0), (Index: AddAudio(), Hz: 880.0) };
writer.BeginWriting();

void Write(int stream, ReadOnlySpan<byte> data, long time, long duration)
{
    using var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
    buffer.Lock(out var pointer, out _, out _);
    unsafe { data.CopyTo(new Span<byte>((void*)pointer, data.Length)); }
    buffer.Unlock();
    buffer.CurrentLength = data.Length;
    using var sample = MediaFactory.MFCreateSample();
    sample.AddBuffer(buffer);
    sample.SampleTime = time;
    sample.SampleDuration = duration;
    writer.WriteSample(stream, sample);
}

var clock = Stopwatch.StartNew();
var frame = new byte[width * height * 4];
using (var file = File.OpenRead(input))
{
    for (var index = 0; index < frames; index++)
    {
        file.ReadExactly(frame);
        Write(video, frame, index * 10_000_000L / Fps, 10_000_000L / Fps);
    }
}

// One second of audio per chunk for the clip's length.
var seconds = (int)Math.Ceiling(frames / (double)Fps);
foreach (var (index, hz) in tracks)
{
    for (var second = 0; second < seconds; second++)
    {
        var pcm = new short[SampleRate * 2];
        for (var i = 0; i < SampleRate; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * hz * ((second * SampleRate) + i) / SampleRate) * 8000);
            pcm[i * 2] = value;
            pcm[(i * 2) + 1] = value;
        }

        Write(index, MemoryMarshal.AsBytes(pcm.AsSpan()), second * 10_000_000L, 10_000_000L);
    }
}

writer.Finalize();
var elapsed = clock.Elapsed.TotalSeconds;
Console.WriteLine($"{frames} frames {width}x{height} in {elapsed:0.00}s = {frames / elapsed:0.0} fps, {new FileInfo(output).Length / 1024} KB");
MediaFactory.MFShutdown();
```
