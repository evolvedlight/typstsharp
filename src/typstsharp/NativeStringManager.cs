using System.Buffers;
using System.Runtime.InteropServices;

namespace typstsharp;

public sealed class NativeStringManager : IDisposable
{
    public const int DefaultCapacity = 64;

    private IntPtr[]? _nativeStringPointers;
    private int _currentIndex = 0;

    public NativeStringManager()
    {
        _nativeStringPointers = ArrayPool<IntPtr>.Shared.Rent(DefaultCapacity);
        _nativeStringPointers.AsSpan().Clear();
    }

    public IntPtr GetNativeUtf8FromString(string? str)
    {
        var ptr = Marshal.StringToCoTaskMemUTF8(str);
        AddPointer(ptr);

        return ptr;
    }

    private void AddPointer(IntPtr ptr)
    {
        if (_nativeStringPointers is null)
        {
            throw new ObjectDisposedException(nameof(NativeStringManager));
        }

        if (_currentIndex >= _nativeStringPointers.Length)
        {
            var tempArray = _nativeStringPointers;
            _nativeStringPointers = ArrayPool<IntPtr>.Shared.Rent(_nativeStringPointers.Length * 2);
            tempArray.AsSpan(0, _currentIndex).CopyTo(_nativeStringPointers);

            ArrayPool<IntPtr>.Shared.Return(tempArray);
        }

        _nativeStringPointers[_currentIndex++] = ptr;
    }

    public void Dispose()
    {
        InternalDispose();
        GC.SuppressFinalize(this);
    }

    private void InternalDispose()
    {
        if (_nativeStringPointers is { Length: > 0 } && _currentIndex > 0)
        {
            for (var i = 0; i < _currentIndex; i++)
            {
                Marshal.FreeCoTaskMem(_nativeStringPointers[i]);
#if DEBUG
                DisposedPointers.Add(_nativeStringPointers[i]);
#endif
            }

            ArrayPool<IntPtr>.Shared.Return(_nativeStringPointers);
            _currentIndex = 0;
            _nativeStringPointers = null;
        }
    }

    ~NativeStringManager()
    {
        InternalDispose();
    }

#if DEBUG
    public List<IntPtr> DisposedPointers { get; } = new();
    public int CurrentCount => _currentIndex;
    public ReadOnlySpan<IntPtr> DebugNativeStringPointers => _nativeStringPointers.AsSpan(0, _currentIndex);
    public IntPtr[]? NativeStringPointersArray => _nativeStringPointers;
#endif
}