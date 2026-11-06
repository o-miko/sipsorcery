//-----------------------------------------------------------------------------
// Filename: IStreamVideoEncoder.cs
//
// Description: Optional capability interface for video encoders that can write
// each encoded frame straight to a stream supplied by the caller, rather than
// returning a newly allocated array for every frame.
//
// Author(s):
// SIPSorcery contributors
//
// History:
// 06 Nov 2026  SIPSorcery contributors  Created.
//
// License:
// BSD 3-Clause "New" or "Revised" License and the additional
// BDS BY-NC-SA restriction, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.IO;

namespace SIPSorceryMedia.Abstractions;

/// <summary>
/// A video encoder that can write each encoded frame directly to a stream supplied by the caller, instead of
/// returning a new array for every frame as <see cref="IVideoEncoder.EncodeVideo"/> does.
/// </summary>
/// <remarks>
/// <para>
/// This is an optional capability. Existing <see cref="IVideoEncoder"/> implementations are not required to
/// implement it, so callers should test for it and fall back to <see cref="IVideoEncoder.EncodeVideo"/>. A
/// <see cref="MemoryStream"/> re-used for every frame gives an encode that does not allocate per frame, and its
/// buffer can be sent without copying it:
/// </para>
/// <code>
/// if (encoder is IStreamVideoEncoder streamEncoder)
/// {
///     reusedStream.SetLength(0);
///     if (streamEncoder.TryEncodeVideo(width, height, frame, pixelFormat, codec, reusedStream, out int length))
///     {
///         rtpSession.SendVideo(durationRtpUnits, new ReadOnlySpan&lt;byte&gt;(reusedStream.GetBuffer(), 0, length));
///     }
/// }
/// else
/// {
///     byte[] encoded = encoder.EncodeVideo(width, height, frame, pixelFormat, codec);
/// }
/// </code>
/// <para>
/// The bytes written for a frame are the same bytes <see cref="IVideoEncoder.EncodeVideo"/> would have returned for
/// it. Each call that returns true writes exactly one complete encoded frame (for H264 and H265 one Annex B access
/// unit, for VP8 and VP9 one frame, for AV1 one temporal unit) and nothing else, so frame boundaries are the call
/// boundaries. A stream re-used for several frames must therefore be reset, or read up to the reported length,
/// between calls.
/// </para>
/// <para>
/// Memory lifetime: the output is written synchronously, on the calling thread, before the method returns. The
/// encoder keeps no reference to the stream or to the input sample after the call, and never exposes its own
/// internal or native buffers to the stream beyond the duration of the write.
/// </para>
/// <para>
/// Errors are reported by throwing, as they are for <see cref="IVideoEncoder.EncodeVideo"/>. If the stream itself
/// throws, the exception propagates to the caller. The encoder stays usable, but the frame that was being written is
/// lost and the stream may hold part of it. Later frames can depend on the lost one, so call
/// <see cref="IVideoEncoder.ForceKeyFrame"/> if the receiver needs to resynchronise.
/// </para>
/// </remarks>
public interface IStreamVideoEncoder : IVideoEncoder
{
    /// <summary>
    /// Encodes a raw video frame and writes the encoded frame to a stream.
    /// </summary>
    /// <param name="width">The width of the raw frame in pixels.</param>
    /// <param name="height">The height of the raw frame in pixels.</param>
    /// <param name="sample">The raw frame, in <paramref name="pixelFormat"/> with no row padding.</param>
    /// <param name="pixelFormat">The pixel format of <paramref name="sample"/>.</param>
    /// <param name="codec">The codec to encode with.</param>
    /// <param name="output">
    /// The stream to write the encoded frame to. It only has to be writable: it does not need to be seekable or
    /// readable, and the encoder never asks it for an underlying buffer. The encoded frame is written at the
    /// stream's current position.
    /// </param>
    /// <param name="bytesWritten">The number of bytes written to <paramref name="output"/>, 0 when the method returns false.</param>
    /// <returns>
    /// True if an encoded frame was written. False if the encoder accepted the input but produced no output for it,
    /// for example because it is still buffering input (FFmpeg's EAGAIN), the frame was dropped by rate limiting, or
    /// the encoder has been disposed. Nothing is written to <paramref name="output"/> when false is returned. A frame
    /// that was buffered is delivered by a later call, in order.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="output"/> is not writable or <paramref name="sample"/> is too small for the frame.</exception>
    bool TryEncodeVideo(int width, int height, ReadOnlySpan<byte> sample, VideoPixelFormatsEnum pixelFormat, VideoCodecsEnum codec, Stream output, out int bytesWritten);
}
