//-----------------------------------------------------------------------------
// Filename: Vp8NetVideoEncoderEndPointUnitTest.cs
//
// Description: Unit tests for the encoded sample events of
// Vp8NetVideoEncoderEndPoint.
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
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Vpx.Net.UnitTest
{
    public class Vp8NetVideoEncoderEndPointUnitTest
    {
        private static readonly Vp8GoldenScenario Scenario = Vp8GoldenScenario.Get("I420_176x144_Gop4_IntraFallback_Forced");

        private static void Feed(Vp8NetVideoEncoderEndPoint endPoint, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                endPoint.ExternalVideoSourceRawSample(33, Scenario.Width, Scenario.Height, Scenario.CreateFrame(i), VideoPixelFormatsEnum.I420);
            }
        }

        private static Vp8NetVideoEncoderEndPoint CreateEndPoint() => new Vp8NetVideoEncoderEndPoint
        {
            KeyframeIntervalFrames = Scenario.KeyframeInterval,
            BaseQIndex = Scenario.BaseQIndex,
            EnableIntraFallback = Scenario.IntraFallback,
        };

        /// <summary>
        /// The span event delivers exactly the frames, and durations, the array event delivers.
        /// </summary>
        [Fact]
        public void OnVideoSourceEncodedSampleFaster_DeliversTheSameFramesAsTheArrayEvent()
        {
            var expected = new List<(uint, byte[])>();
            var arrayEndPoint = CreateEndPoint();
            arrayEndPoint.OnVideoSourceEncodedSample += (duration, sample) => expected.Add((duration, sample));
            Feed(arrayEndPoint, 8);

            var actual = new List<(uint, byte[])>();
            var spanEndPoint = CreateEndPoint();
            spanEndPoint.OnVideoSourceEncodedSampleFaster += (duration, sample) => actual.Add((duration, sample.ToArray()));
            Assert.True(spanEndPoint.HasEncodedVideoSubscribers());
            Feed(spanEndPoint, 8);

            Assert.Equal(8, expected.Count);
            Assert.Equal(expected.ConvertAll(e => e.Item1), actual.ConvertAll(a => a.Item1));
            Assert.Equal(expected.ConvertAll(e => e.Item2), actual.ConvertAll(a => a.Item2));
        }

        /// <summary>
        /// With both events subscribed each frame is encoded once and raised on both, the array event getting a copy
        /// of its own.
        /// </summary>
        [Fact]
        public void BothEventsSubscribed_EachFrameRaisedOnBoth()
        {
            var fromSpan = new List<byte[]>();
            var fromArray = new List<byte[]>();
            var endPoint = CreateEndPoint();
            endPoint.OnVideoSourceEncodedSampleFaster += (duration, sample) => fromSpan.Add(sample.ToArray());
            endPoint.OnVideoSourceEncodedSample += (duration, sample) => fromArray.Add(sample);

            Feed(endPoint, 6);

            Assert.Equal(6, fromArray.Count);
            Assert.Equal(fromArray, fromSpan);

            var reference = new List<byte[]>();
            var referenceEndPoint = CreateEndPoint();
            referenceEndPoint.OnVideoSourceEncodedSample += (duration, sample) => reference.Add(sample);
            Feed(referenceEndPoint, 6);
            Assert.Equal(reference, fromArray);
        }

        /// <summary>
        /// Raising frames on the span event does not allocate an array per frame.
        /// </summary>
        [Fact]
        public void OnVideoSourceEncodedSampleFaster_DoesNotAllocateTheFrame()
        {
            long encodedBytes = 0;
            var endPoint = CreateEndPoint();
            endPoint.OnVideoSourceEncodedSampleFaster += (duration, sample) => encodedBytes += sample.Length;

            var frames = new[] { Scenario.CreateFrame(0), Scenario.CreateFrame(1) };
            for (int i = 0; i < 6; i++)
            {
                endPoint.ExternalVideoSourceRawSample(33, Scenario.Width, Scenario.Height, frames[i & 1], VideoPixelFormatsEnum.I420);
            }

            encodedBytes = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10; i++)
            {
                endPoint.ExternalVideoSourceRawSample(33, Scenario.Width, Scenario.Height, frames[i & 1], VideoPixelFormatsEnum.I420);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(encodedBytes > 10 * 100);
            Assert.True(allocated < encodedBytes / 10, $"Allocated {allocated} bytes for {encodedBytes} encoded bytes.");
        }
    }
}
