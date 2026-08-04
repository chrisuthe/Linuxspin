using Sendspin.Core.Audio;
using Xunit;

namespace Sendspin.Tests;

/// <summary>
/// Tests for the unmanaged single-producer/single-consumer ring.
/// </summary>
/// <remarks>
/// Worth testing carefully: it is read from a realtime audio callback, where an off-by-one in the
/// wrap arithmetic is a click or a buffer of garbage rather than an exception, and would be very
/// hard to attribute after the fact.
/// </remarks>
public sealed class UnmanagedAudioRingTests
{
    [Fact]
    public void NewRing_IsEmpty()
    {
        using var ring = new UnmanagedAudioRing(16);

        Assert.Equal(16, ring.Capacity);
        Assert.Equal(0, ring.Available);
        Assert.Equal(16, ring.FreeSpace);
        Assert.Equal(0, ring.TotalRead);
    }

    [Fact]
    public void WriteThenRead_RoundTripsSamplesInOrder()
    {
        using var ring = new UnmanagedAudioRing(8);
        float[] source = [1f, 2f, 3f, 4f];

        Assert.Equal(4, ring.Write(source));
        Assert.Equal(4, ring.Available);

        var destination = new float[4];
        Assert.Equal(4, ring.Read(destination));
        Assert.Equal(source, destination);
        Assert.Equal(0, ring.Available);
        Assert.Equal(4, ring.TotalRead);
    }

    [Fact]
    public void Write_StopsAtCapacityRatherThanOverwriting()
    {
        using var ring = new UnmanagedAudioRing(4);

        Assert.Equal(4, ring.Write([1f, 2f, 3f, 4f, 5f, 6f]));
        Assert.Equal(0, ring.FreeSpace);
        Assert.Equal(0, ring.Write([7f]));

        var destination = new float[4];
        ring.Read(destination);
        Assert.Equal([1f, 2f, 3f, 4f], destination);
    }

    /// <summary>
    /// Exercises a read and a write that both straddle the end of the buffer.
    /// </summary>
    [Fact]
    public void ReadAndWrite_WrapCorrectly()
    {
        using var ring = new UnmanagedAudioRing(4);

        ring.Write([1f, 2f, 3f]);

        var first = new float[2];
        ring.Read(first);
        Assert.Equal([1f, 2f], first);

        // Write index is at 3, read index at 2: this write wraps past the end.
        Assert.Equal(3, ring.Write([4f, 5f, 6f]));
        Assert.Equal(4, ring.Available);

        var second = new float[4];
        Assert.Equal(4, ring.Read(second));
        Assert.Equal([3f, 4f, 5f, 6f], second);
    }

    /// <summary>
    /// A short read must still leave the device with a full buffer.
    /// </summary>
    /// <remarks>
    /// Returning a partially-filled buffer would leave whatever the device had there before, which
    /// is a burst of stale audio rather than the silence an underrun should sound like.
    /// </remarks>
    [Fact]
    public void Read_ZeroFillsTheShortfall()
    {
        using var ring = new UnmanagedAudioRing(8);
        ring.Write([1f, 2f]);

        var destination = new float[] { 9f, 9f, 9f, 9f };
        Assert.Equal(2, ring.Read(destination));
        Assert.Equal([1f, 2f, 0f, 0f], destination);
    }

    [Fact]
    public void Read_OnEmptyRing_ReturnsSilence()
    {
        using var ring = new UnmanagedAudioRing(8);

        var destination = new float[] { 5f, 5f };
        Assert.Equal(0, ring.Read(destination));
        Assert.Equal([0f, 0f], destination);
    }

    [Fact]
    public void Clear_DiscardsBufferedSamples()
    {
        using var ring = new UnmanagedAudioRing(8);
        ring.Write([1f, 2f, 3f]);

        ring.Clear();

        Assert.Equal(0, ring.Available);
        Assert.Equal(3, ring.TotalRead);
    }

    /// <summary>
    /// Runs a producer and a consumer concurrently and checks that every sample arrives exactly
    /// once and in order.
    /// </summary>
    /// <remarks>
    /// Not a proof of lock-freedom, but it does catch the mistakes that matter: publishing an
    /// index before the data it refers to, and losing or duplicating samples across a wrap.
    /// </remarks>
    [Fact]
    public async Task ConcurrentProducerAndConsumer_PreserveEverySampleInOrder()
    {
        const int total = 100_000;
        using var ring = new UnmanagedAudioRing(512);

        var producer = Task.Run(() =>
        {
            var next = 0;
            var chunk = new float[64];

            while (next < total)
            {
                var size = Math.Min(chunk.Length, total - next);
                for (var i = 0; i < size; i++)
                {
                    chunk[i] = next + i;
                }

                var written = ring.Write(chunk.AsSpan(0, size));
                next += written;

                if (written == 0)
                {
                    Thread.SpinWait(50);
                }
            }
        });

        var consumer = Task.Run(() =>
        {
            var expected = 0;
            var chunk = new float[48];

            while (expected < total)
            {
                var read = ring.Read(chunk);

                for (var i = 0; i < read; i++)
                {
                    Assert.Equal(expected + i, chunk[i]);
                }

                expected += read;

                if (read == 0)
                {
                    Thread.SpinWait(50);
                }
            }

            return expected;
        });

        await producer;
        Assert.Equal(total, await consumer);
    }
}
