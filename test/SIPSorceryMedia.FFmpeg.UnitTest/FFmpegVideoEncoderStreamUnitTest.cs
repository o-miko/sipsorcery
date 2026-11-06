//-----------------------------------------------------------------------------
// Filename: FFmpegVideoEncoderStreamUnitTest.cs
//
// Description: Unit tests for the FFmpegVideoEncoder method that writes each
// encoded frame straight from FFmpeg's packet to a stream, rather than copying
// it into a new array, for the span event of FFmpegVideoEndPoint, and for the
// encoder's argument checks, statistics and reset handling.
//
// The tests encode with the native FFmpeg libraries and are skipped when those
// cannot be loaded, see FFmpegAvailability.
//
// Author(s):
// SIPSorcery contributors
//
// History:
// 06 Nov 2026  SIPSorcery contributors  Created.
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace SIPSorceryMedia.FFmpeg.UnitTest
{
    public class FFmpegVideoEncoderStreamUnitTest
    {
        private const int WIDTH = 320;
        private const int HEIGHT = 240;
        private const int FRAMES = 12;

        /// <summary>
        /// Creates an encoder configured to produce the same bytes every time for the same input, so two instances
        /// can be compared: a single encoding thread, and for libvpx a fixed speed (a positive cpu-used in realtime
        /// mode means libvpx picks the speed from how long frames take to encode).
        /// </summary>
        private static FFmpegVideoEncoder CreateEncoder(VideoCodecsEnum codec, Dictionary<string, string> extraOptions = null)
        {
            var options = new Dictionary<string, string>();
            if (codec == VideoCodecsEnum.VP8)
            {
                options["cpu-used"] = "-5";
            }
            if (extraOptions != null)
            {
                foreach (var option in extraOptions)
                {
                    options[option.Key] = option.Value;
                }
            }

            var encoder = new FFmpegVideoEncoder(options);
            encoder.SetThreadCount(1);
            return encoder;
        }

        public static IEnumerable<object[]> CodecsAndFormats()
        {
            foreach (var codec in new[] { VideoCodecsEnum.H264, VideoCodecsEnum.VP8 })
            {
                foreach (var format in new[] { VideoPixelFormatsEnum.I420, VideoPixelFormatsEnum.Bgr })
                {
                    yield return new object[] { codec, format };
                }
            }
        }

        /// <summary>
        /// Encodes the test sequence with the array returning method, forcing a key frame part way through.
        /// </summary>
        private static List<byte[]> EncodeWithArrays(VideoCodecsEnum codec, VideoPixelFormatsEnum format, int frames = FRAMES)
        {
            using (var encoder = CreateEncoder(codec))
            {
                var encoded = new List<byte[]>();
                for (int i = 0; i < frames; i++)
                {
                    if (i == 7)
                    {
                        encoder.ForceKeyFrame();
                    }
                    encoded.Add(encoder.EncodeVideo(WIDTH, HEIGHT, TestFrames.Create(WIDTH, HEIGHT, format, i), format, codec));
                }
                return encoded;
            }
        }

        /// <summary>
        /// Every frame written to a re-used stream is exactly the frame the array method returns, key frames, forced
        /// key frame and inter frames alike.
        /// </summary>
        [FFmpegTheory("libx264", "libvpx")]
        [MemberData(nameof(CodecsAndFormats))]
        public void TryEncodeVideo_Stream_MatchesEncodeVideo(VideoCodecsEnum codec, VideoPixelFormatsEnum format)
        {
            var expected = EncodeWithArrays(codec, format);

            using (var encoder = CreateEncoder(codec))
            {
                var stream = new MemoryStream();
                for (int i = 0; i < FRAMES; i++)
                {
                    if (i == 7)
                    {
                        encoder.ForceKeyFrame();
                    }

                    stream.SetLength(0);
                    bool written = encoder.TryEncodeVideo(WIDTH, HEIGHT, TestFrames.Create(WIDTH, HEIGHT, format, i), format, codec, stream, out int bytesWritten);

                    Assert.Equal(expected[i] != null, written);
                    Assert.Equal(expected[i]?.Length ?? 0, bytesWritten);
                    Assert.Equal(expected[i] ?? Array.Empty<byte>(), stream.ToArray());
                }
            }

            AssertKeyFrames(codec, expected);
        }

        /// <summary>
        /// The end point's span event delivers exactly the frames its array event does, for raw frames supplied as
        /// arrays and as <see cref="RawImage"/>s in memory the GC does not own. With both events subscribed each frame
        /// is raised on both.
        /// </summary>
        [FFmpegFact("libx264")]
        public void EndPoint_SpanEvent_MatchesArrayEvent()
        {
            const VideoPixelFormatsEnum format = VideoPixelFormatsEnum.Bgr;
            var arrayFrames = new List<byte[]>();
            var spanFrames = new List<byte[]>();
            var bothArrayFrames = new List<byte[]>();
            var bothSpanFrames = new List<byte[]>();

            using (var arrayEndPoint = new FFmpegVideoEndPoint())
            using (var spanEndPoint = new FFmpegVideoEndPoint())
            using (var bothEndPoint = new FFmpegVideoEndPoint())
            {
                arrayEndPoint.OnVideoSourceEncodedSample += (duration, sample) => arrayFrames.Add(sample);
                spanEndPoint.OnVideoSourceEncodedSampleFaster += (duration, sample) => spanFrames.Add(sample.ToArray());
                bothEndPoint.OnVideoSourceEncodedSample += (duration, sample) => bothArrayFrames.Add(sample);
                bothEndPoint.OnVideoSourceEncodedSampleFaster += (duration, sample) => bothSpanFrames.Add(sample.ToArray());

                var endPoints = new[] { arrayEndPoint, spanEndPoint, bothEndPoint };
                foreach (var endPoint in endPoints)
                {
                    endPoint.SetVideoSourceFormat(new VideoFormat(VideoCodecsEnum.H264, 96));
                }

                for (int i = 0; i < 6; i++)
                {
                    var frame = TestFrames.Create(WIDTH, HEIGHT, format, i);

                    if (i % 2 == 0)
                    {
                        foreach (var endPoint in endPoints)
                        {
                            endPoint.ExternalVideoSourceRawSample(33, WIDTH, HEIGHT, frame, format);
                        }
                        continue;
                    }

                    IntPtr native = Marshal.AllocHGlobal(frame.Length);
                    try
                    {
                        Marshal.Copy(frame, 0, native, frame.Length);
                        var rawImage = new RawImage { Width = WIDTH, Height = HEIGHT, Stride = WIDTH * 3, Sample = native, PixelFormat = format };

                        foreach (var endPoint in endPoints)
                        {
                            endPoint.ExternalVideoSourceRawSampleFaster(33, rawImage);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(native);
                    }
                }
            }

            Assert.Equal(6, arrayFrames.Count);
            Assert.Equal(arrayFrames, spanFrames);
            Assert.Equal(arrayFrames, bothArrayFrames);
            Assert.Equal(arrayFrames, bothSpanFrames);
        }

        /// <summary>
        /// Frames written one after another without resetting the stream are laid out back to back, each call
        /// reporting what it added, so frame boundaries are kept.
        /// </summary>
        [FFmpegFact("libx264")]
        public void TryEncodeVideo_ReusedStreamNotReset_AppendsWholeFrames()
        {
            var expected = EncodeWithArrays(VideoCodecsEnum.H264, VideoPixelFormatsEnum.I420);

            using (var encoder = CreateEncoder(VideoCodecsEnum.H264))
            {
                var stream = new MemoryStream();
                var lengths = new List<int>();

                for (int i = 0; i < FRAMES; i++)
                {
                    if (i == 7)
                    {
                        encoder.ForceKeyFrame();
                    }
                    long before = stream.Position;
                    encoder.TryEncodeVideo(WIDTH, HEIGHT, TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, i), VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264, stream, out int bytesWritten);
                    Assert.Equal(stream.Position - before, bytesWritten);
                    lengths.Add(bytesWritten);
                }

                byte[] all = stream.ToArray();
                int offset = 0;
                for (int i = 0; i < FRAMES; i++)
                {
                    Assert.Equal(expected[i] ?? Array.Empty<byte>(), all.AsSpan(offset, lengths[i]).ToArray());
                    offset += lengths[i];
                }
                Assert.Equal(all.Length, offset);
            }
        }

        /// <summary>
        /// A stream that can only be written to, with no position, length, seeking or reading, is enough, and each
        /// frame arrives in a single write.
        /// </summary>
        [FFmpegFact("libvpx")]
        public void TryEncodeVideo_WriteOnlyNonSeekableStream_ReceivesEachFrameInOneWrite()
        {
            var expected = EncodeWithArrays(VideoCodecsEnum.VP8, VideoPixelFormatsEnum.I420);

            using (var encoder = CreateEncoder(VideoCodecsEnum.VP8))
            {
                var stream = new WriteOnlyStream();
                int framesWritten = 0;

                for (int i = 0; i < FRAMES; i++)
                {
                    if (i == 7)
                    {
                        encoder.ForceKeyFrame();
                    }
                    if (encoder.TryEncodeVideo(WIDTH, HEIGHT, TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, i), VideoPixelFormatsEnum.I420, VideoCodecsEnum.VP8, stream, out _))
                    {
                        framesWritten++;
                    }
                }

                var concatenated = new MemoryStream();
                foreach (var frame in expected)
                {
                    if (frame != null)
                    {
                        concatenated.Write(frame, 0, frame.Length);
                    }
                }

                Assert.Equal(concatenated.ToArray(), stream.ToArray());
                Assert.Equal(framesWritten, stream.WriteCalls);
            }
        }

        /// <summary>
        /// An encoder that holds frames back (libx264 with rate control look-ahead, i.e. without the zerolatency tune)
        /// reports false, and writes nothing, until it starts producing output; the frames then arrive in order and
        /// match the array method, which returns null for the same frames. (libvpx ignores lag-in-frames in its
        /// realtime mode, so it cannot be used for this.)
        /// </summary>
        [FFmpegFact("libx264")]
        public void TryEncodeVideo_EncoderBuffersInput_ReturnsFalseAndWritesNothingUntilOutput()
        {
            const int frames = 24;
            var lookAhead = new Dictionary<string, string> { ["tune"] = "film", ["x264-params"] = "rc-lookahead=4" };
            var expected = new List<byte[]>();

            using (var reference = CreateEncoder(VideoCodecsEnum.H264, lookAhead))
            {
                for (int i = 0; i < frames; i++)
                {
                    expected.Add(reference.EncodeVideo(WIDTH, HEIGHT, TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, i), VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264));
                }
            }

            // The encoder really does hold frames back, and then outputs them.
            Assert.Null(expected[0]);
            Assert.NotNull(expected[frames - 1]);
            Assert.True(EncodedFrames.H264HasIdr(expected.Find(f => f != null)));

            using (var encoder = CreateEncoder(VideoCodecsEnum.H264, lookAhead))
            {
                var stream = new MemoryStream();

                for (int i = 0; i < frames; i++)
                {
                    long lengthBefore = stream.Length;
                    bool written = encoder.TryEncodeVideo(WIDTH, HEIGHT, TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, i), VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264, stream, out int bytesWritten);

                    if (expected[i] == null)
                    {
                        Assert.False(written);
                        Assert.Equal(0, bytesWritten);
                        Assert.Equal(lengthBefore, stream.Length);
                    }
                    else
                    {
                        Assert.True(written);
                        Assert.Equal(expected[i], stream.ToArray().AsSpan((int)lengthBefore).ToArray());
                    }
                }
            }
        }

        /// <summary>
        /// A stream that cannot be written to is rejected before encoding, so no frame is consumed by the encoder and
        /// lost: the next frame encoded is the first frame the encoder has seen, a key frame identical to the
        /// reference encoder's first.
        /// </summary>
        [FFmpegFact("libx264")]
        public void TryEncodeVideo_ReadOnlyStream_ThrowsWithoutConsumingAFrame()
        {
            var expected = EncodeWithArrays(VideoCodecsEnum.H264, VideoPixelFormatsEnum.I420, frames: 2);

            using (var encoder = CreateEncoder(VideoCodecsEnum.H264))
            {
                var frame0 = TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, 0);
                Assert.Throws<ArgumentException>(() => encoder.TryEncodeVideo(WIDTH, HEIGHT, frame0, VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264, new MemoryStream(new byte[4], writable: false), out _));
                Assert.Throws<ArgumentNullException>(() => encoder.TryEncodeVideo(WIDTH, HEIGHT, frame0, VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264, null, out _));

                var stream = new MemoryStream();
                Assert.True(encoder.TryEncodeVideo(WIDTH, HEIGHT, frame0, VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264, stream, out _));
                Assert.Equal(expected[0], stream.ToArray());
            }
        }

        /// <summary>
        /// FFmpeg reads the sample through a pointer, so a sample shorter than the frame is refused rather than read
        /// past its end, whether it is a span or an array.
        /// </summary>
        [FFmpegFact("libx264")]
        public void SampleTooSmallOrNull_Throws()
        {
            using (var encoder = CreateEncoder(VideoCodecsEnum.H264))
            {
                var tooSmall = new byte[WIDTH * HEIGHT];
                Assert.Throws<ArgumentException>(() => encoder.TryEncodeVideo(WIDTH, HEIGHT, tooSmall, VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264, new MemoryStream(), out _));
                Assert.Throws<ArgumentException>(() => encoder.TryEncodeVideo(WIDTH, HEIGHT, new byte[WIDTH * HEIGHT * 3 - 1], VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.H264, new MemoryStream(), out _));
                Assert.Throws<ArgumentException>(() => encoder.EncodeVideo(WIDTH, HEIGHT, tooSmall, VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264));
                Assert.Throws<ArgumentNullException>(() => encoder.EncodeVideo(WIDTH, HEIGHT, null, VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264));
            }
        }

        /// <summary>
        /// Subscribing to the encoder statistics before the first frame, when there is no encoder context yet, does not
        /// stop that frame being encoded.
        /// </summary>
        [FFmpegFact("libx264")]
        public void EncodeVideo_StatisticsSubscribedBeforeFirstFrame_Encodes()
        {
            using (var encoder = CreateEncoder(VideoCodecsEnum.H264))
            {
                int statistics = 0;
                encoder.OnVideoEncoderStatistics += (sender, stats) => statistics++;

                var frame = TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, 0);

                Assert.NotNull(encoder.EncodeVideo(WIDTH, HEIGHT, frame, VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264));
                Assert.Equal(1, statistics);
            }
        }

        /// <summary>
        /// Changing the bitrate re-creates the encoder, and leaves a decoder in use on the same instance, as an end point
        /// that both sends and receives has, decoding.
        /// </summary>
        [FFmpegFact("libx264")]
        public void SetBitrate_WhileDecoding_DecoderKeepsDecoding()
        {
            var encoded = EncodeWithArrays(VideoCodecsEnum.H264, VideoPixelFormatsEnum.I420, frames: 4);

            using (var codec = CreateEncoder(VideoCodecsEnum.H264))
            {
                Assert.NotNull(codec.EncodeVideo(WIDTH, HEIGHT, TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, 0), VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264));
                Assert.NotEmpty(codec.DecodeVideo(encoded[0], VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.H264));

                codec.SetBitrate(500_000, null, null, null);

                Assert.NotEmpty(codec.DecodeVideo(encoded[1], VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.H264));
                Assert.NotNull(codec.EncodeVideo(WIDTH, HEIGHT, TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, 1), VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264));
            }
        }

        /// <summary>
        /// When the destination fails the exception reaches the caller and the encoder keeps working; a key frame can
        /// be forced to resynchronise a receiver that missed the lost frame.
        /// </summary>
        [FFmpegTheory("libx264", "libvpx")]
        [InlineData(VideoCodecsEnum.H264)]
        [InlineData(VideoCodecsEnum.VP8)]
        public void TryEncodeVideo_StreamThrows_ExceptionPropagatesAndEncoderRecovers(VideoCodecsEnum codec)
        {
            using (var encoder = CreateEncoder(codec))
            {
                var stream = new MemoryStream();
                Assert.True(encoder.TryEncodeVideo(WIDTH, HEIGHT, TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, 0), VideoPixelFormatsEnum.I420, codec, stream, out _));

                var failing = new FailingStream();
                Assert.Throws<IOException>(() => encoder.TryEncodeVideo(WIDTH, HEIGHT, TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, 1), VideoPixelFormatsEnum.I420, codec, failing, out _));
                Assert.Equal(1, failing.WriteAttempts);

                encoder.ForceKeyFrame();
                stream.SetLength(0);
                Assert.True(encoder.TryEncodeVideo(WIDTH, HEIGHT, TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, 2), VideoPixelFormatsEnum.I420, codec, stream, out _));

                var frame = stream.ToArray();
                Assert.True(codec == VideoCodecsEnum.H264 ? EncodedFrames.H264HasIdr(frame) : EncodedFrames.Vp8IsKeyFrame(frame));
            }
        }

        /// <summary>
        /// A disposed encoder writes nothing and reports no output, as the array method returns null.
        /// </summary>
        [FFmpegFact("libx264")]
        public void TryEncodeVideo_AfterDispose_ReturnsFalse()
        {
            var encoder = CreateEncoder(VideoCodecsEnum.H264);
            var frame = TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, 0);
            var stream = new MemoryStream();
            Assert.True(encoder.TryEncodeVideo(WIDTH, HEIGHT, frame, VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264, stream, out _));

            encoder.Dispose();

            stream.SetLength(0);
            Assert.False(encoder.TryEncodeVideo(WIDTH, HEIGHT, frame, VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264, stream, out int bytesWritten));
            Assert.Equal(0, bytesWritten);
            Assert.Equal(0, stream.Length);
            Assert.Null(encoder.EncodeVideo(WIDTH, HEIGHT, frame, VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264));
        }

        /// <summary>
        /// Disposing while another thread is encoding waits for the frame in progress, rather than freeing the codec
        /// context and packet underneath it.
        /// </summary>
        [FFmpegFact("libx264")]
        public void Dispose_WhileEncodingOnAnotherThread_IsSafe()
        {
            var frame = TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, 0);

            for (int run = 0; run < 20; run++)
            {
                var encoder = CreateEncoder(VideoCodecsEnum.H264);
                using (var started = new ManualResetEventSlim())
                {
                    var encodeTask = Task.Run(() =>
                    {
                        var stream = new MemoryStream();
                        for (int i = 0; i < 200; i++)
                        {
                            stream.SetLength(0);
                            encoder.TryEncodeVideo(WIDTH, HEIGHT, frame, VideoPixelFormatsEnum.I420, VideoCodecsEnum.H264, stream, out _);
                            started.Set();
                        }
                    });

                    started.Wait(TimeSpan.FromSeconds(10));
                    Thread.Sleep(run % 5);
                    encoder.Dispose();

                    encodeTask.Wait(TimeSpan.FromSeconds(30));
                    Assert.True(encodeTask.IsCompleted);
                    Assert.Null(encodeTask.Exception);
                }
            }
        }

        /// <summary>
        /// Once the destination has grown to the frame size, encoding to it allocates nothing per frame; the array
        /// method allocates at least the encoded frame.
        /// </summary>
        [FFmpegTheory("libx264", "libvpx")]
        [InlineData(VideoCodecsEnum.H264)]
        [InlineData(VideoCodecsEnum.VP8)]
        public void TryEncodeVideo_ReusedDestination_DoesNotAllocatePerFrame(VideoCodecsEnum codec)
        {
            using (var encoder = CreateEncoder(codec))
            {
                var frames = new[] { TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, 0), TestFrames.Create(WIDTH, HEIGHT, VideoPixelFormatsEnum.I420, 1) };
                var stream = new MemoryStream(256 * 1024);

                for (int i = 0; i < 12; i++)
                {
                    stream.SetLength(0);
                    encoder.TryEncodeVideo(WIDTH, HEIGHT, frames[i & 1], VideoPixelFormatsEnum.I420, codec, stream, out _);
                }

                const int count = 40;
                long encodedBytes = 0;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < count; i++)
                {
                    stream.SetLength(0);
                    encoder.TryEncodeVideo(WIDTH, HEIGHT, frames[i & 1], VideoPixelFormatsEnum.I420, codec, stream, out int written);
                    encodedBytes += written;
                }
                long allocatedPerFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / count;

                Assert.True(encodedBytes > 0);
                Assert.True(allocatedPerFrame < 64, $"Allocated {allocatedPerFrame} bytes per frame.");

                long arrayBefore = GC.GetAllocatedBytesForCurrentThread();
                var array = encoder.EncodeVideo(WIDTH, HEIGHT, frames[0], VideoPixelFormatsEnum.I420, codec);
                Assert.True(GC.GetAllocatedBytesForCurrentThread() - arrayBefore >= (array?.Length ?? 0));
            }
        }

        /// <summary>
        /// Checks the encoded sequence has the key frames the settings ask for: the first frame and the forced one.
        /// </summary>
        private static void AssertKeyFrames(VideoCodecsEnum codec, List<byte[]> frames)
        {
            Func<byte[], bool> isKey = codec == VideoCodecsEnum.H264 ? (Func<byte[], bool>)EncodedFrames.H264HasIdr : EncodedFrames.Vp8IsKeyFrame;

            Assert.True(isKey(frames[0]));
            Assert.True(isKey(frames[7]));
            Assert.False(isKey(frames[1]));
            Assert.False(isKey(frames[8]));
        }
    }
}
