using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace typstsharp;

/// <summary>
/// The result of a single Typst compilation, holding the rendered output in the memory allocated by
/// the native Typst library.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is copied onto the managed heap while the document is alive: the output can be read as a
/// <see cref="ReadOnlySpan{T}"/>, streamed, or written straight to disk. This keeps a multi-megabyte
/// PDF off the large object heap, which matters when documents are rendered in a loop or on a server.
/// </para>
/// <para>
/// A document is a list of output buffers, not of pages. PDF export produces exactly one buffer
/// containing the whole document however many pages it has; PNG and SVG export produce one buffer
/// per page. See <see cref="OutputCount"/>.
/// </para>
/// <para>
/// The native memory is released by <see cref="Dispose"/>. A <see cref="ReadOnlySpan{T}"/> from
/// <see cref="GetOutputSpan"/> points into that memory and carries no reference back to the
/// document, so it is only valid inside the scope that holds the document. Streams from
/// <see cref="OpenOutputStream"/> do keep the document alive and throw once it is disposed.
/// <see cref="GetOutputBytes"/> and <see cref="RentOutput"/> return memory that survives disposal.
/// </para>
/// <para>
/// Reading a document from several threads at once is safe; disposing it while another thread reads
/// is not. Concurrent calls to <see cref="Dispose"/> are safe.
/// </para>
/// </remarks>
public sealed class TypstDocument : IDisposable
{
    private unsafe CsBindgen.CompileResult _native;
    private readonly int _outputCount;
    private readonly IReadOnlyList<string> _warnings;
    private readonly long _nativeByteCount;
    private int _disposed;

    internal unsafe TypstDocument(CsBindgen.CompileResult native)
    {
        int outputCount = checked((int)native.buffers_len);
        if (outputCount > 0 && native.buffers == null)
        {
            throw new InvalidOperationException("The Typst compiler reported output buffers but returned none.");
        }

        int warningCount = checked((int)native.warnings_len);
        if (warningCount > 0 && native.warnings == null)
        {
            throw new InvalidOperationException("The Typst compiler reported warnings but returned none.");
        }

        // Warnings are small and are copied eagerly so that they stay usable after disposal.
        var warnings = new string[warningCount];
        for (int i = 0; i < warnings.Length; i++)
        {
            var warning = native.warnings[i];
            warnings[i] = warning.message_ptr != null
                ? System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(warning.message_ptr, checked((int)warning.message_len)))
                : string.Empty;
        }

        long nativeByteCount = 0;
        for (int i = 0; i < outputCount; i++)
        {
            nativeByteCount += (long)native.buffers[i].len;
        }

        _outputCount = outputCount;
        _warnings = Array.AsReadOnly(warnings);
        _nativeByteCount = nativeByteCount;

        // Taking ownership must be the last thing that happens. An object with a finalizer is queued
        // for finalization when it is allocated, so a constructor that threw after this point would
        // leave behind a finalizer that frees a result the caller has already freed.
        _native = native;

        // The rendered bytes live outside the managed heap and are invisible to the GC. Without this
        // a leaked document would exert no collection pressure at all.
        if (nativeByteCount > 0)
        {
            GC.AddMemoryPressure(nativeByteCount);
        }
    }

    /// <summary>
    /// The number of output buffers. This is 1 for PDF export regardless of how many pages the
    /// document has, and one per page for PNG and SVG export.
    /// </summary>
    public int OutputCount => _outputCount;

    /// <summary>Warnings reported by the Typst compiler.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// The length in bytes of one output buffer, without touching its content. Useful for setting a
    /// response Content-Length before streaming the document.
    /// </summary>
    /// <param name="output">The zero-based buffer index; defaults to the only buffer of a PDF.</param>
    public unsafe long GetOutputLength(int output = 0)
    {
        long length = (long)GetBuffer(output).len;
        KeepAlive();
        return length;
    }

    /// <summary>
    /// Gets an output buffer as a view over the native memory, without copying.
    /// </summary>
    /// <param name="output">The zero-based buffer index; defaults to the only buffer of a PDF.</param>
    /// <returns>
    /// A span that carries no reference back to the document. It is only valid inside the scope that
    /// holds the document, which a <c>using</c> declaration guarantees. To hand the bytes to code
    /// that outlives this scope, use <see cref="OpenOutputStream"/>, <see cref="RentOutput"/> or
    /// <see cref="GetOutputBytes"/> instead.
    /// </returns>
    public unsafe ReadOnlySpan<byte> GetOutputSpan(int output = 0)
    {
        var buffer = GetBuffer(output);
        return new ReadOnlySpan<byte>(buffer.ptr, ToLength(buffer.len));
    }

    /// <summary>
    /// Opens a read-only, seekable stream over an output buffer. The stream reads directly from the
    /// native memory, keeps the document alive for as long as it is referenced, and throws
    /// <see cref="ObjectDisposedException"/> once the document has been disposed.
    /// </summary>
    /// <param name="output">The zero-based buffer index; defaults to the only buffer of a PDF.</param>
    public unsafe Stream OpenOutputStream(int output = 0)
    {
        var buffer = GetBuffer(output);
        return new OutputStream(this, buffer.ptr, (long)buffer.len);
    }

    /// <summary>
    /// Writes an output buffer to <paramref name="destination"/> without an intermediate managed buffer.
    /// </summary>
    /// <param name="destination">The stream to write to.</param>
    /// <param name="output">The zero-based buffer index; defaults to the only buffer of a PDF.</param>
    public unsafe void CopyOutputTo(Stream destination, int output = 0)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(GetOutputSpan(output));
        KeepAlive();
    }

    /// <summary>
    /// Asynchronously writes an output buffer to <paramref name="destination"/> without an
    /// intermediate managed buffer.
    /// </summary>
    /// <param name="destination">The stream to write to.</param>
    /// <param name="output">The zero-based buffer index; defaults to the only buffer of a PDF.</param>
    /// <param name="cancellationToken">Token to cancel the write.</param>
    /// <remarks>
    /// The destination must not retain the memory it is handed past the completion of the write; it
    /// points into memory this document owns.
    /// </remarks>
    public async ValueTask CopyOutputToAsync(Stream destination, int output = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var manager = CreateBufferManager(output);
        await destination.WriteAsync(manager.Memory, cancellationToken).ConfigureAwait(false);
        KeepAlive();
    }

    /// <summary>
    /// Writes an output buffer straight to a file, overwriting it if it exists. The bytes go from
    /// native memory to the file handle, so nothing proportional to the document size is allocated
    /// on the managed heap.
    /// </summary>
    /// <param name="path">The file to create or overwrite.</param>
    /// <param name="output">The zero-based buffer index; defaults to the only buffer of a PDF.</param>
    public unsafe void WriteOutputToFile(string path, int output = 0)
    {
        var content = GetOutputSpan(output);
        using (var handle = OpenFile(path, content.Length, FileOptions.None))
        {
            RandomAccess.Write(handle, content, fileOffset: 0);
        }

        KeepAlive();
    }

    /// <summary>
    /// Asynchronously writes an output buffer straight to a file, overwriting it if it exists,
    /// without an intermediate managed buffer.
    /// </summary>
    /// <param name="path">The file to create or overwrite.</param>
    /// <param name="output">The zero-based buffer index; defaults to the only buffer of a PDF.</param>
    /// <param name="cancellationToken">Token to cancel the write. A cancelled write leaves a partial file behind.</param>
    public async Task WriteOutputToFileAsync(string path, int output = 0, CancellationToken cancellationToken = default)
    {
        using var manager = CreateBufferManager(output);
        using var handle = OpenFile(path, manager.Memory.Length, FileOptions.Asynchronous);
        await RandomAccess.WriteAsync(handle, manager.Memory, fileOffset: 0, cancellationToken).ConfigureAwait(false);
        KeepAlive();
    }

    /// <summary>
    /// Copies an output buffer into a buffer rented from <see cref="ArrayPool{T}"/>. Use this when
    /// the bytes have to outlive the document but the allocation should be recycled rather than
    /// collected.
    /// </summary>
    /// <param name="output">The zero-based buffer index; defaults to the only buffer of a PDF.</param>
    /// <returns>
    /// An owner whose <see cref="IMemoryOwner{T}.Memory"/> is exactly the length of the buffer.
    /// Dispose it to return the memory to the pool, and do not touch the memory afterwards.
    /// </returns>
    public unsafe IMemoryOwner<byte> RentOutput(int output = 0)
    {
        var content = GetOutputSpan(output);
        var owner = new PooledBuffer(content.Length);
        content.CopyTo(owner.Span);
        KeepAlive();
        return owner;
    }

    /// <summary>
    /// Copies an output buffer into a newly allocated array. Prefer <see cref="GetOutputSpan"/>,
    /// <see cref="WriteOutputToFile"/> or <see cref="RentOutput"/> when the copy can be avoided.
    /// </summary>
    /// <param name="output">The zero-based buffer index; defaults to the only buffer of a PDF.</param>
    public unsafe byte[] GetOutputBytes(int output = 0)
    {
        var bytes = GetOutputSpan(output).ToArray();
        KeepAlive();
        return bytes;
    }

    private static SafeFileHandle OpenFile(string path, long length, FileOptions options) =>
        File.OpenHandle(
            path,
            FileMode.Create,
            FileAccess.Write,
            // A concurrent reader is not locked out, matching File.WriteAllBytes.
            FileShare.Read,
            options,
            preallocationSize: length);

    /// <summary>
    /// Wraps an output buffer in a <see cref="MemoryManager{T}"/> so that the asynchronous overloads,
    /// which cannot be declared in an unsafe context, can pass the native memory around as
    /// <see cref="Memory{T}"/>.
    /// </summary>
    private unsafe NativeBufferMemoryManager CreateBufferManager(int output)
    {
        var buffer = GetBuffer(output);
        return new NativeBufferMemoryManager(this, buffer.ptr, ToLength(buffer.len));
    }

    private unsafe CsBindgen.Buffer GetBuffer(int output)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        if ((uint)output >= (uint)_outputCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(output),
                output,
                $"The document has {_outputCount} output buffer(s).");
        }

        return _native.buffers[output];
    }

    /// <summary>
    /// Narrows a native buffer length to the <see cref="int"/> the span-based members are limited to.
    /// A bare <see cref="OverflowException"/> would say nothing about how to proceed.
    /// </summary>
    private static int ToLength(nuint length)
    {
        if (length > int.MaxValue)
        {
            throw new NotSupportedException(
                $"The output buffer is {length} bytes. Buffers larger than {int.MaxValue} bytes can only be read through OpenOutputStream.");
        }

        return (int)length;
    }

    /// <summary>
    /// Keeps the document reachable until the native buffer has actually been read. Without this the
    /// collector may consider the document dead right after <see cref="GetBuffer"/> returned and run
    /// the finalizer, freeing the memory that is still being read from.
    /// </summary>
    private void KeepAlive() => GC.KeepAlive(this);

    /// <summary>Releases the native memory held by this document.</summary>
    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~TypstDocument()
    {
        Free();
    }

    private unsafe void Free()
    {
        // Exchange rather than a plain bool check: a double free corrupts the native heap, which is a
        // far worse outcome than the torn reads the rest of the type tolerates.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CsBindgen.NativeMethods.free_compile_result(_native);
        _native = default;
        if (_nativeByteCount > 0)
        {
            GC.RemoveMemoryPressure(_nativeByteCount);
        }
    }

    /// <summary>
    /// A read-only stream over one output buffer that holds its document alive. Handing out a bare
    /// <see cref="UnmanagedMemoryStream"/> would let the document be finalized, and its memory freed,
    /// while the stream was still being read.
    /// </summary>
    private sealed unsafe class OutputStream : UnmanagedMemoryStream
    {
        private readonly TypstDocument _owner;

        internal OutputStream(TypstDocument owner, byte* pointer, long length)
            : base(pointer, length, length, FileAccess.Read)
        {
            _owner = owner;
        }

        // Only the byte-array reads are overridden. Every other read path on Stream and
        // UnmanagedMemoryStream, span and async alike, ends up calling one of these two, so this
        // covers them all. Overriding Read(Span) as well would recurse: the UnmanagedMemoryStream
        // override delegates to Stream.Read(Span) for any derived type, and that implementation
        // calls back into Read(byte[]).
        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);
            int read = base.Read(buffer, offset, count);
            GC.KeepAlive(_owner);
            return read;
        }

        public override int ReadByte()
        {
            ObjectDisposedException.ThrowIf(_owner.IsDisposed, _owner);
            int value = base.ReadByte();
            GC.KeepAlive(_owner);
            return value;
        }
    }
}
