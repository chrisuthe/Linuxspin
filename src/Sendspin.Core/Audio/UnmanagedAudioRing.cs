using System.Runtime.InteropServices;

namespace Sendspin.Core.Audio;

/// <summary>
/// A single-producer, single-consumer ring of 32-bit float samples held in unmanaged
/// memory.
/// </summary>
/// <remarks>
/// <para>
/// This exists for backends whose render callback is invoked by the OS on a realtime
/// thread (CoreAudio's AUHAL). Such a callback must not allocate, lock, or touch managed
/// state that the GC might move or suspend behind. It can copy out of a fixed buffer. So
/// the managed side fills this ring ahead of the deadline and the callback does nothing
/// but <see cref="Read"/>.
/// </para>
/// <para>
/// Backends that own their own render thread (WASAPI's push loop, OpenAL's buffer queue)
/// do not need this: there is no OS callback to keep clean, and they read the sample
/// source directly on the thread they created.
/// </para>
/// <para>
/// Memory is unmanaged so the consumer's pointer stays valid across collections; a pinned
/// managed array would keep the address stable but pin a GC region for the lifetime of
/// playback.
/// </para>
/// <para>
/// Correctness rests on exactly one producer and exactly one consumer. The indices are
/// free-running <see cref="long"/> counters, so they are never ambiguous between "full"
/// and "empty" and never wrap in any realistic runtime.
/// </para>
/// </remarks>
public sealed unsafe class UnmanagedAudioRing : IDisposable
{
    private readonly int _capacity;
    private float* _buffer;

    // Free-running counters, not offsets: write - read is the fill level directly.
    private long _writeCount;
    private long _readCount;

    private bool _disposed;

    /// <summary>
    /// Creates a ring holding <paramref name="capacitySamples"/> samples.
    /// </summary>
    /// <param name="capacitySamples">
    /// Capacity in samples, counting every channel separately. Must be positive.
    /// </param>
    public UnmanagedAudioRing(int capacitySamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacitySamples, 0);

        _capacity = capacitySamples;
        _buffer = (float*)NativeMemory.AlignedAlloc((nuint)capacitySamples * sizeof(float), 64);
        NativeMemory.Fill(_buffer, (nuint)capacitySamples * sizeof(float), 0);
    }

    /// <summary>
    /// Gets the ring capacity in samples.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets the samples currently readable. Safe to call from either side; the value is a
    /// lower bound for the consumer and an upper bound for the producer.
    /// </summary>
    public int Available
    {
        get
        {
            var written = Volatile.Read(ref _writeCount);
            var read = Volatile.Read(ref _readCount);
            return (int)(written - read);
        }
    }

    /// <summary>
    /// Gets the space currently writable.
    /// </summary>
    public int FreeSpace => _capacity - Available;

    /// <summary>
    /// Gets the total samples the consumer has taken since construction. This is the
    /// frame counter a render callback publishes as its position.
    /// </summary>
    public long TotalRead => Volatile.Read(ref _readCount);

    /// <summary>
    /// Copies as much of <paramref name="source"/> into the ring as fits.
    /// Producer side only.
    /// </summary>
    /// <returns>The number of samples written, which is less than the source length
    /// when the ring is nearly full.</returns>
    public int Write(ReadOnlySpan<float> source)
    {
        var buffer = _buffer;
        if (buffer is null)
        {
            return 0;
        }

        var written = Volatile.Read(ref _writeCount);
        var read = Volatile.Read(ref _readCount);
        var free = _capacity - (int)(written - read);
        var toWrite = Math.Min(source.Length, free);
        if (toWrite <= 0)
        {
            return 0;
        }

        var offset = (int)(written % _capacity);
        var firstChunk = Math.Min(toWrite, _capacity - offset);

        source[..firstChunk].CopyTo(new Span<float>(buffer + offset, firstChunk));
        if (firstChunk < toWrite)
        {
            source[firstChunk..toWrite].CopyTo(new Span<float>(buffer, toWrite - firstChunk));
        }

        // Publish the data before the index that makes it visible.
        Volatile.Write(ref _writeCount, written + toWrite);
        return toWrite;
    }

    /// <summary>
    /// Copies up to <paramref name="destination"/>.Length samples out of the ring,
    /// zero-filling any shortfall so the caller always hands the device a full buffer.
    /// Consumer side only, and realtime-safe.
    /// </summary>
    /// <returns>The number of real samples copied, before zero-fill.</returns>
    public int Read(Span<float> destination)
    {
        var buffer = _buffer;
        if (buffer is null)
        {
            destination.Clear();
            return 0;
        }

        var written = Volatile.Read(ref _writeCount);
        var read = Volatile.Read(ref _readCount);
        var available = (int)(written - read);
        var toRead = Math.Min(destination.Length, available);

        if (toRead > 0)
        {
            var offset = (int)(read % _capacity);
            var firstChunk = Math.Min(toRead, _capacity - offset);

            new ReadOnlySpan<float>(buffer + offset, firstChunk).CopyTo(destination);
            if (firstChunk < toRead)
            {
                new ReadOnlySpan<float>(buffer, toRead - firstChunk).CopyTo(destination[firstChunk..]);
            }

            Volatile.Write(ref _readCount, read + toRead);
        }

        if (toRead < destination.Length)
        {
            destination[toRead..].Clear();
        }

        return toRead;
    }

    /// <summary>
    /// Discards buffered samples. Both sides must be stopped: this moves the read index
    /// and so is not safe against a live consumer.
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _readCount, Volatile.Read(ref _writeCount));
    }

    /// <summary>
    /// Releases the buffer. The consumer must have stopped first.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var buffer = _buffer;
        _buffer = null;

        if (buffer is not null)
        {
            NativeMemory.AlignedFree(buffer);
        }
    }
}
