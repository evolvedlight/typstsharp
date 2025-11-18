using CsBindgen;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace typstsharp
{
    public record Fonts(
        bool IncludeSystemFonts = true,
        IEnumerable<string>? FontPaths = null
    );

    

    public unsafe class TypstClient : IDisposable
    {
        public static string EmptyDictionaryJson => "{}";
        private CsBindgen.Compiler* _compiler;
        private bool _disposed = false;

        public TypstClient(string input, Fonts? fonts = null, Dictionary<string, string>? sysInputs = null, string? root = null)
        {
            fonts ??= new Fonts();
            var fontPaths = fonts.FontPaths ?? Enumerable.Empty<string>();
            bool ignoreSystemFonts = !fonts.IncludeSystemFonts;

            var inputPtr = Marshal.StringToHGlobalAnsi(input);
            IntPtr rootPtr = IntPtr.Zero;
            if (!string.IsNullOrWhiteSpace(root))
            {
                rootPtr = Marshal.StringToHGlobalAnsi(root);
            }

            var fontPathsList = fontPaths.ToList();
            var fontPathPtrs = new IntPtr[fontPathsList.Count];
            for (int i = 0; i < fontPathsList.Count; i++)
            {
                fontPathPtrs[i] = Marshal.StringToHGlobalAnsi(fontPathsList[i]);
            }

            var sysInputsJson = EmptyDictionaryJson;
            var sysInputsPtr = Marshal.StringToHGlobalAnsi(sysInputsJson);

            try
            {
                fixed (IntPtr* fontPathsRawPtr = fontPathPtrs)
                {
                    IntPtr* fontPathsPtr = fontPathsList.Count == 0 ? null : fontPathsRawPtr;
                    _compiler = CsBindgen.NativeMethods.create_compiler((byte*)rootPtr, (byte*)inputPtr, (byte**)fontPathsPtr, (nuint)fontPathsList.Count, (byte*)sysInputsPtr, ignoreSystemFonts);
                }

                if (_compiler == null)
                {
                    throw new Exception("Failed to create Typst compiler.");
                }
            }
            finally
            {
                if (rootPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(rootPtr);
                }
                Marshal.FreeHGlobal(inputPtr);
                foreach (var ptr in fontPathPtrs) Marshal.FreeHGlobal(ptr);
                Marshal.FreeHGlobal(sysInputsPtr);
            }
        }

        public CompileOutcome Compile()
        {
            EnsureNotDisposed();

            var native = CsBindgen.NativeMethods.compile(_compiler);
            try
            {
                if (native.error != null)
                {
                    var error = Marshal.PtrToStringAnsi((nint)native.error) ?? "Unknown Typst error";
                    throw new InvalidOperationException(error);
                }

                var managedBuffers = new List<byte[]>((int)native.buffers_len);
                if (native.buffers != null)
                {
                    for (nuint i = 0; i < native.buffers_len; i++)
                    {
                        var buffer = native.buffers[i];
                        var managed = new byte[checked((int)buffer.len)];
                        if (buffer.len > 0 && buffer.ptr != null)
                        {
                            Marshal.Copy((IntPtr)buffer.ptr, managed, 0, managed.Length);
                        }
                        managedBuffers.Add(managed);
                    }
                }

                var managedWarnings = new List<string>((int)native.warnings_len);
                if (native.warnings != null)
                {
                    for (nuint i = 0; i < native.warnings_len; i++)
                    {
                        var warning = native.warnings[i];
                        managedWarnings.Add(Marshal.PtrToStringAnsi((nint)warning.message) ?? string.Empty);
                    }
                }

                return new CompileOutcome(managedBuffers, managedWarnings);
            }
            finally
            {
                CsBindgen.NativeMethods.free_compile_result(native);
                CsBindgen.NativeMethods.reset_world();
            }
        }

        public record TypstWarning(string Message);

        public (List<byte[]> pages, List<TypstWarning> warnings) Compile(string format = "pdf", float ppi = 144.0f)
        {
            var outcome = Compile();
            var pages = new List<byte[]>(outcome.Buffers);
            var warnings = outcome.Warnings
                .Select(message => new TypstWarning(message))
                .ToList();
            return (pages, warnings);
        }

        public void Compile(string outputFile, string format, float ppi = 144.0f)
        {
            var (pages, _) = Compile(format, ppi);
            if (pages.Count == 1)
            {
                File.WriteAllBytes(outputFile, pages[0]);
            }
            else
            {
                var extension = Path.GetExtension(outputFile);
                var fileName = Path.GetFileNameWithoutExtension(outputFile);
                var directory = Path.GetDirectoryName(outputFile) ?? "";

                for (int i = 0; i < pages.Count; i++)
                {
                    var pagePath = Path.Combine(directory, $"{fileName}-{i + 1}{extension}");
                    File.WriteAllBytes(pagePath, pages[i]);
                }
            }
        }

        public void SetSysInputs(Dictionary<string, string> inputs)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TypstClient));

            var sysInputsJson = JsonSerializer.Serialize(inputs ?? new Dictionary<string, string>());
            var sysInputsPtr = Marshal.StringToHGlobalAnsi(sysInputsJson);
            try
            {
                var ok = CsBindgen.NativeMethods.set_sys_inputs(_compiler, (byte*)sysInputsPtr);
                if (!ok)
                {
                    throw new Exception("Failed to set system inputs");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(sysInputsPtr);
            }
        }


        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (_compiler != null)
            {
                CsBindgen.NativeMethods.free_compiler(_compiler);
                _compiler = null;
            }

            _disposed = true;
        }

        ~TypstClient()
        {
            Dispose(false);
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TypstClient));
            }
        }
    }

    public sealed record CompileOutcome(IReadOnlyList<byte[]> Buffers, IReadOnlyList<string> Warnings);
    public sealed record AllocationSnapshot(ulong BufferCount, ulong BufferBytes, ulong WarningCount, ulong WarningBytes);
}
