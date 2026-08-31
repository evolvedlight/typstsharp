using System.Buffers;

namespace typstsharp;

/// <summary>
/// Exposes a block of Typst-owned native memory as <see cref="Memory{T}"/> so that it can be handed
/// to asynchronous APIs, which need <see cref="ReadOnlyMemory{T}"/> rather than a span, without
/// copying it onto the managed heap first.
/// </summary>
/// <remarks>
/// The memory is owned by the native side; this manager neither allocates nor frees it. It holds a
/// reference to the owning <see cref="TypstDocument"/> so that the document cannot be finalized, and
/// its memory freed, while a write from this buffer is still in flight.
/// </remarks>
internal sealed unsafe class NativeBufferMemoryManager : MemoryManager<byte>
{
    private readonly TypstDocument _owner;
    private readonly byte* _pointer;
    private readonly int _length;

    public NativeBufferMemoryManager(TypstDocument owner, byte* pointer, int length)
    {
        _owner = owner;
        _pointer = pointer;
        _length = length;
    }

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);
        return new Span<byte>(_pointer, _length);
    }

    /// <summary>
    /// Native memory is never relocated by the GC, so pinning is a no-op and only has to hand back
    /// the address of the requested element. One past the end is allowed, matching the semantics of
    /// <see cref="Memory{T}"/> over an array.
    /// </summary>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);

        if ((uint)elementIndex > (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        return new MemoryHandle(_pointer + elementIndex);
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        GC.KeepAlive(_owner);
    }
}

/// <summary>
/// An <see cref="IMemoryOwner{T}"/> backed by <see cref="ArrayPool{T}.Shared"/>, exposing exactly the
/// requested number of bytes rather than the (potentially larger) rented array.
/// </summary>
/// <remarks>
/// The buffer is cleared before it goes back to the pool. <see cref="ArrayPool{T}.Shared"/> is
/// process-wide and shared with everything else in the host, and what we put in it is a rendered
/// document, so leaving the content behind for an unrelated component to rent would be a disclosure
/// waiting to happen. Only the used prefix is cleared, so the cost is proportional to the document
/// rather than to the (rounded up) rented array.
/// </remarks>
internal sealed class PooledBuffer : IMemoryOwner<byte>
{
    private byte[]? _array;
    private readonly int _length;

    public PooledBuffer(int length)
    {
        _array = ArrayPool<byte>.Shared.Rent(length);
        _length = length;
    }

    public Memory<byte> Memory => new(Array, 0, _length);

    public Span<byte> Span => new(Array, 0, _length);

    private byte[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledBuffer));

    public void Dispose()
    {
        var array = Interlocked.Exchange(ref _array, null);
        if (array is not null)
        {
            array.AsSpan(0, _length).Clear();
            ArrayPool<byte>.Shared.Return(array);
        }
    }
}
