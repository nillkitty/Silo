using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Silo.Providers.Archive;
using Silo.Providers.Reflection;
using Telefrag.DI;

namespace Silo.Providers.Format.Containers;

/// <summary>
///     A Windows cabinet (<c>.cab</c>), read and written with the cabinet library that comes with Windows (cabinet.dll: FDI to
///     read, FCI to write, MSZIP compression). A cabinet holds files only: a folder exists in it while it has files in it.
/// </summary>
[Singleton(typeof(IFileFormat))]
public class CabFormat() : RebuiltContainerFormat("Cabinet File", "cab", "application/vnd.ms-cab-compressed"), IContainerFormat
{
    public override bool CanWrite => OperatingSystem.IsWindows();

    public override IReadOnlyList<ContainerEntry> List(string container, CancellationToken cancel = default)
    {
        var entries = new List<ContainerEntry>();
        Cabinet.Extract(container, file =>
                                   {
                                       cancel.ThrowIfCancellationRequested();
                                       entries.Add(new ContainerEntry(file.Name, false, file.Size, file.Modified));
                                       return null;
                                   });
        return ContainerPaths.Complete(entries);
    }

    public override Stream OpenEntry(string container, string entry)
    {
        string  wanted = ContainerPaths.Normalize(entry);
        Stream? found  = null;
        Cabinet.Extract(container, file =>
                                   {
                                       if (found is not null || !string.Equals(ContainerPaths.Normalize(file.Name), wanted, StringComparison.OrdinalIgnoreCase))
                                           return null;

                                       return found = ContainerPaths.NewBuffer(file.Size);
                                   }, keepOpen: true);

        if (found is null) throw new FileNotFoundException($"\"{entry}\" is not in the cabinet.", entry);

        found.Position = 0;
        return found;
    }

    void IContainerFormat.ExtractTo(string container, string directory, string? entry, CancellationToken cancel)
    {
        string  root   = Path.GetFullPath(directory);
        string? prefix = entry is null ? null : ContainerPaths.Normalize(entry);
        Cabinet.Extract(container, file =>
                                   {
                                       cancel.ThrowIfCancellationRequested();
                                       string path = ContainerPaths.Normalize(file.Name);
                                       if (prefix is { Length: > 0 } && !ContainerPaths.IsSameOrInside(path, prefix)) return null;

                                       string relative = prefix is { Length: > 0 } ? ContainerPaths.Relative(path, prefix, true) : path;
                                       if (ContainerPaths.Resolve(root, relative) is not { } target) return null;

                                       Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                                       return File.Create(target);
                                   });
    }

    public override void CreateFrom(string container, string directory, CancellationToken cancel = default)
    {
        var root  = new DirectoryInfo(directory);
        var files = root.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                        .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                        .Select(f => (f.FullName, Path.GetRelativePath(root.FullName, f.FullName)))
                        .ToList();
        Cabinet.Create(container, files, cancel);
    }
}

/// <summary>The cabinet library of Windows (cabinet.dll), wrapped: what is needed to take files out of a cabinet and to make one.</summary>
/// <remarks>
///     The library does its own file handling through callbacks. Every name it is given here is a token (a short ASCII name)
///     that stands for a real path or stream kept on this side, so paths with any characters in them work; the names of the
///     files inside the cabinet are written in UTF-8 (and marked so) when they are not plain ASCII.
/// </remarks>
public static class Cabinet
{
    /// <summary>A file in a cabinet, as it is being read.</summary>
    public sealed record FileInfo(string Name, long Size, DateTimeOffset? Modified);

    private const int  NameIsUtf8  = 0x80; // _A_NAME_IS_UTF
    private const int  CpuUnknown  = -1;
    private const int  CompressMszip = 1;  // tcompTYPE_MSZIP

    // ---- reading ------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Goes through the files of a cabinet: <paramref name="target" /> is asked, for each, where to write it (a stream, or null
    ///     to skip it). The streams are closed when the file is written, unless <paramref name="keepOpen" />.
    /// </summary>
    public static void Extract(string cabinet, Func<FileInfo, Stream?> target, bool keepOpen = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cabinets are read with cabinet.dll, which only Windows has.");
        if (IsEmpty(cabinet)) return;

        using var session = new Handles();
        session.Map("cab", () => new FileStream(cabinet, FileMode.Open, FileAccess.Read, FileShare.Read));

        Exception? failure = null;
        var        erf     = Marshal.AllocHGlobal(16);
        try
        {
            var callbacks = session.FdiCallbacks();
            var notify = new FdiNotify((type, info) =>
                                       {
                                           try
                                           {
                                               switch (type)
                                               {
                                                   case 2: // fdintCOPY_FILE
                                                   {
                                                       var    n    = Marshal.PtrToStructure<FdiNotification>(info);
                                                       string name = AnsiOrUtf8(n.psz1, (n.attribs & NameIsUtf8) != 0);
                                                       var    file = new FileInfo(name.Replace('\\', '/'), n.cb, DosDate(n.date, n.time));
                                                       return target(file) is { } stream ? session.Add(stream, keepOpen) : IntPtr.Zero;
                                                   }
                                                   case 3: // fdintCLOSE_FILE_INFO
                                                   {
                                                       var n = Marshal.PtrToStructure<FdiNotification>(info);
                                                       session.Close(n.hf);
                                                       return 1;
                                                   }
                                                   default:
                                                       return IntPtr.Zero;
                                               }
                                           }
                                           catch (Exception ex)
                                           {
                                               failure ??= ex;
                                               return -1; // stops the library
                                           }
                                       });

            var fdi = FDICreate(callbacks.Alloc, callbacks.Free, callbacks.Open, callbacks.Read, callbacks.Write, callbacks.Close, callbacks.Seek,
                                CpuUnknown, erf);
            if (fdi == IntPtr.Zero) throw new IOException("The cabinet library could not be started.");

            try
            {
                bool ok = FDICopy(fdi, "cab", string.Empty, 0, notify, IntPtr.Zero, IntPtr.Zero);
                GC.KeepAlive(notify);
                GC.KeepAlive(callbacks);
                if (failure is not null) throw failure is OperationCanceledException ? failure : new IOException(failure.Message, failure);
                if (!ok) throw new InvalidDataException($"The cabinet could not be read ({FdiError(Marshal.ReadInt32(erf))}).");
            }
            finally
            {
                FDIDestroy(fdi);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(erf);
        }
    }

    // ---- writing ------------------------------------------------------------------------------------------------

    /// <summary>Makes a cabinet of the files given (each with the name it is to have inside: '\' or '/' between folders), in one folder, MSZIP-compressed.</summary>
    public static void Create(string cabinet, IReadOnlyList<(string Source, string Name)> files, CancellationToken cancel = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cabinets are made with cabinet.dll, which only Windows has.");

        if (files.Count == 0)
        {
            WriteEmpty(cabinet);
            return;
        }

        using var session = new Handles();
        session.Map("out.cab", () => new FileStream(cabinet, FileMode.Create, FileAccess.ReadWrite, FileShare.None));

        Exception? failure = null;
        var        erf     = Marshal.AllocHGlobal(16);
        try
        {
            var callbacks = session.FciCallbacks();
            var ccab = new Ccab
                       {
                           cb             = 0x7FFFFFFF,
                           cbFolderThresh = 0x7FFFFFFF,
                           szDisk         = new byte[256],
                           szCab          = Ascii("out.cab", 256),
                           szCabPath      = new byte[256]
                       };

            FciFilePlaced placed  = (_, _, _, _, _) => 0;
            FciNextCabinet next   = (_, _, _) => true;
            FciStatus     status  = (_, _, _, _) => 0;
            int           current = -1;
            FciOpenInfo openInfo = (pszName, pdate, ptime, pattribs, err, _) =>
                                   {
                                       try
                                       {
                                           var (source, name) = files[current];
                                           var info = new global::System.IO.FileInfo(source);
                                           var (date, time) = ToDos(info.LastWriteTime);
                                           Marshal.WriteInt16(pdate, (short)date);
                                           Marshal.WriteInt16(ptime, (short)time);
                                           int attributes = (int)(info.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive));
                                           if (!IsAscii(name)) attributes |= NameIsUtf8;
                                           Marshal.WriteInt16(pattribs, (short)attributes);
                                           return session.Add(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read), false);
                                       }
                                       catch (Exception ex)
                                       {
                                           failure ??= ex;
                                           Marshal.WriteInt32(err, 1);
                                           return -1;
                                       }
                                   };

            var fci = FCICreate(erf, placed, callbacks.Alloc, callbacks.Free, callbacks.FciOpen, callbacks.FciRead, callbacks.FciWrite, callbacks.FciClose,
                                callbacks.FciSeek, callbacks.FciDelete, callbacks.FciTemp, ref ccab, IntPtr.Zero);
            if (fci == IntPtr.Zero) throw new IOException("The cabinet library could not be started.");

            try
            {
                for (current = 0; current < files.Count; current++)
                {
                    cancel.ThrowIfCancellationRequested();
                    string name = files[current].Name.Replace('/', '\\');
                    if (!FCIAddFile(fci, Ascii("src", 8), Utf8(name), false, next, status, openInfo, CompressMszip))
                        throw (Exception?)(failure as OperationCanceledException) ??
                              new IOException($"\"{name}\" could not be added to the cabinet ({FciError(Marshal.ReadInt32(erf))}).", failure);
                }

                if (!FCIFlushCabinet(fci, false, next, status))
                    throw new IOException($"The cabinet could not be written ({FciError(Marshal.ReadInt32(erf))}).", failure);
            }
            finally
            {
                FCIDestroy(fci);
                GC.KeepAlive(callbacks);
                GC.KeepAlive(placed);
                GC.KeepAlive(next);
                GC.KeepAlive(status);
                GC.KeepAlive(openInfo);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(erf);
        }
    }

    /// <summary>
    ///     A cabinet with nothing in it: just the header, which says there are no folders and no files. The library will not make
    ///     one (it writes nothing until it has a file).
    /// </summary>
    private static void WriteEmpty(string cabinet)
    {
        const int headerSize = 36;
        var       header     = new byte[headerSize];
        "MSCF"u8.CopyTo(header);
        BitConverter.TryWriteBytes(header.AsSpan(8),  headerSize); // cbCabinet
        BitConverter.TryWriteBytes(header.AsSpan(16), headerSize); // coffFiles
        header[24] = 3;                                            // versionMinor
        header[25] = 1;                                            // versionMajor
        File.WriteAllBytes(cabinet, header);
    }

    /// <summary>Whether a cabinet has files in it at all (an empty one cannot be read by the library, which expects at least one folder).</summary>
    public static bool IsEmpty(string cabinet)
    {
        using var file = File.OpenRead(cabinet);
        var       head = new byte[36];
        if (file.Read(head, 0, head.Length) < head.Length || !head.AsSpan(0, 4).SequenceEqual("MSCF"u8)) return false;

        return BitConverter.ToUInt16(head, 26) == 0 && BitConverter.ToUInt16(head, 28) == 0;
    }

    // ---- the handle table ---------------------------------------------------------------------------------------

    /// <summary>The streams the library has open, by the handle it was given for each, and the names it may open.</summary>
    private sealed class Handles : IDisposable
    {
        private readonly Dictionary<string, Func<Stream>> _names   = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<IntPtr, Stream>       _streams = [];
        private readonly HashSet<IntPtr>                  _keep    = [];
        private readonly List<string>                     _temps   = [];
        private          long                             _next    = 1;

        public void Map(string token, Func<Stream> open) => _names[token] = open;

        public IntPtr Add(Stream stream, bool keepOpen)
        {
            var handle = (IntPtr)_next++;
            _streams[handle] = stream;
            if (keepOpen) _keep.Add(handle);
            return handle;
        }

        public void Close(IntPtr handle)
        {
            if (!_streams.Remove(handle, out var stream)) return;

            if (_keep.Remove(handle)) stream.Flush();
            else stream.Dispose();
        }

        private IntPtr Open(IntPtr name)
        {
            string token = Marshal.PtrToStringAnsi(name) ?? string.Empty;
            return _names.TryGetValue(token, out var open) ? Add(open(), false) : -1;
        }

        private int Read(IntPtr handle, IntPtr buffer, int count)
        {
            if (!_streams.TryGetValue(handle, out var stream)) return -1;

            var data = new byte[count];
            int got  = 0;
            while (got < count)
            {
                int n = stream.Read(data, got, count - got);
                if (n <= 0) break;
                got += n;
            }

            Marshal.Copy(data, 0, buffer, got);
            return got;
        }

        private int Write(IntPtr handle, IntPtr buffer, int count)
        {
            if (!_streams.TryGetValue(handle, out var stream)) return -1;

            var data = new byte[count];
            Marshal.Copy(buffer, data, 0, count);
            stream.Write(data, 0, count);
            return count;
        }

        private int Seek(IntPtr handle, int distance, int origin)
        {
            if (!_streams.TryGetValue(handle, out var stream)) return -1;

            return (int)stream.Seek(distance, origin switch { 1 => SeekOrigin.Current, 2 => SeekOrigin.End, _ => SeekOrigin.Begin });
        }

        public Callbacks FdiCallbacks() => new(this);

        public Callbacks FciCallbacks() => new(this);

        /// <summary>The delegates handed to the library, kept here so they live as long as it may call them.</summary>
        public sealed class Callbacks
        {
            public readonly Alloc       Alloc;
            public readonly Free        Free;
            public readonly FdiOpen     Open;
            public readonly FdiRead     Read;
            public readonly FdiRead     Write;
            public readonly FdiClose    Close;
            public readonly FdiSeek     Seek;
            public readonly FciOpen     FciOpen;
            public readonly FciRead     FciRead;
            public readonly FciRead     FciWrite;
            public readonly FciClose    FciClose;
            public readonly FciSeek     FciSeek;
            public readonly FciDelete   FciDelete;
            public readonly FciTempFile FciTemp;

            public Callbacks(Handles h)
            {
                Alloc = size => Marshal.AllocHGlobal(size);
                Free  = memory => Marshal.FreeHGlobal(memory);
                Open  = (name, _, _) => Guard(() => h.Open(name), -1);
                Read  = (handle, buffer, count) => Guard(() => h.Read(handle, buffer, count), -1);
                Write = (handle, buffer, count) => Guard(() => h.Write(handle, buffer, count), -1);
                Close = handle =>
                        {
                            h.Close(handle);
                            return 0;
                        };
                Seek = (handle, distance, origin) => Guard(() => h.Seek(handle, distance, origin), -1);

                FciOpen  = (name, _, _, err, _) => Fci(() => h.Open(name), (IntPtr)(-1), err);
                FciRead  = (handle, buffer, count, err, _) => Fci(() => h.Read(handle, buffer, count), -1, err);
                FciWrite = (handle, buffer, count, err, _) => Fci(() => h.Write(handle, buffer, count), -1, err);
                FciClose = (handle, _, _) =>
                           {
                               h.Close(handle);
                               return 0;
                           };
                FciSeek   = (handle, distance, origin, err, _) => Fci(() => h.Seek(handle, distance, origin), -1, err);
                FciDelete = (name, _, _) => Guard(() => h.Delete(name), -1);
                FciTemp = (buffer, size, _) =>
                          {
                              string token = h.NewTemp();
                              byte[] bytes = Ascii(token, size);
                              Marshal.Copy(bytes, 0, buffer, Math.Min(bytes.Length, size));
                              return true;
                          };
            }

            private static T Guard<T>(Func<T> run, T failed)
            {
                try
                {
                    return run();
                }
                catch (Exception)
                {
                    return failed;
                }
            }

            private static T Fci<T>(Func<T> run, T failed, IntPtr err)
            {
                try
                {
                    return run();
                }
                catch (Exception ex)
                {
                    Marshal.WriteInt32(err, ex.HResult);
                    return failed;
                }
            }
        }

        /// <summary>A temporary file the library asked for: a token for it that opens (and reopens) the real file.</summary>
        private string NewTemp()
        {
            string token = "tmp" + _temps.Count;
            string path  = Path.Combine(Path.GetTempPath(), "silo-cab-" + Guid.NewGuid().ToString("N") + ".tmp");
            _temps.Add(path);
            Map(token, () => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete));
            return token;
        }

        private int Delete(IntPtr name)
        {
            string token = Marshal.PtrToStringAnsi(name) ?? string.Empty;
            if (token.StartsWith("tmp", StringComparison.Ordinal) && int.TryParse(token[3..], out int index) && index < _temps.Count)
                File.Delete(_temps[index]);
            return 0;
        }

        public void Dispose()
        {
            foreach (var (handle, stream) in _streams)
                if (!_keep.Contains(handle))
                    stream.Dispose();
            _streams.Clear();

            foreach (string temp in _temps)
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
        }
    }

    // ---- small helpers ------------------------------------------------------------------------------------------

    private static bool IsAscii(string text) => text.All(c => c < 128);

    private static byte[] Ascii(string text, int size)
    {
        var bytes = new byte[size];
        Encoding.ASCII.GetBytes(text).AsSpan(0, Math.Min(text.Length, size - 1)).CopyTo(bytes);
        return bytes;
    }

    private static byte[] Utf8(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Array.Resize(ref bytes, bytes.Length + 1);
        return bytes;
    }

    private static string AnsiOrUtf8(IntPtr text, bool utf8)
    {
        return (utf8 ? Marshal.PtrToStringUTF8(text) : Marshal.PtrToStringAnsi(text)) ?? string.Empty;
    }

    private static DateTimeOffset? DosDate(ushort date, ushort time)
    {
        try
        {
            if (date == 0) return null;

            return new DateTimeOffset(new DateTime(1980 + (date >> 9), (date >> 5) & 0xF, date & 0x1F, time >> 11, (time >> 5) & 0x3F, (time & 0x1F) * 2,
                                                   DateTimeKind.Local));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static (ushort Date, ushort Time) ToDos(DateTime t)
    {
        if (t.Year < 1980) t = new DateTime(1980, 1, 1);
        return ((ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day), (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)));
    }

    private static string FdiError(int code) => code switch
                                                {
                                                    1  => "the cabinet file could not be found",
                                                    2  => "it is not a cabinet",
                                                    3  => "an unknown cabinet version",
                                                    4  => "the cabinet is damaged",
                                                    5  => "out of memory",
                                                    6  => "an unsupported compression type",
                                                    7  => "the data is damaged",
                                                    8  => "the file is in the wrong cabinet of a set",
                                                    9  => "a file could not be written",
                                                    10 => "the cabinet needs a reserved area the reader cannot handle",
                                                    11 => "the cabinet belongs to a different set",
                                                    12 => "stopped",
                                                    _  => "error " + code
                                                };

    private static string FciError(int code) => code switch
                                                {
                                                    1 => "a file could not be opened",
                                                    2 => "a file could not be read",
                                                    3 => "out of memory",
                                                    4 => "a temporary file could not be made",
                                                    5 => "an unsupported compression type",
                                                    6 => "the cabinet could not be written",
                                                    7 => "stopped",
                                                    8 => "the cabinet is too large",
                                                    _ => "error " + code
                                                };

    // ---- the library --------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FdiNotification
    {
        public int    cb;
        public IntPtr psz1;
        public IntPtr psz2;
        public IntPtr psz3;
        public IntPtr pv;
        public IntPtr hf;
        public ushort date;
        public ushort time;
        public ushort attribs;
        public ushort setID;
        public ushort iCabinet;
        public ushort iFolder;
        public int    fdie;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Ccab
    {
        public uint   cb;
        public uint   cbFolderThresh;
        public uint   cbReserveCFHeader;
        public uint   cbReserveCFFolder;
        public uint   cbReserveCFData;
        public int    iCab;
        public int    iDisk;
        public int    fFailOnIncompressible;
        public ushort setID;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] szDisk;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] szCab;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] szCabPath;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Alloc(int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void   Free(IntPtr memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FdiOpen(IntPtr name, int flags, int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int    FdiRead(IntPtr handle, IntPtr buffer, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int    FdiClose(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int    FdiSeek(IntPtr handle, int distance, int origin);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FdiNotify(int type, IntPtr notification);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int    FciFilePlaced(IntPtr ccab, IntPtr name, int size, bool continuation, IntPtr pv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FciOpen(IntPtr name, int flags, int mode, IntPtr err, IntPtr pv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int    FciRead(IntPtr handle, IntPtr buffer, int count, IntPtr err, IntPtr pv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int    FciClose(IntPtr handle, IntPtr err, IntPtr pv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int    FciSeek(IntPtr handle, int distance, int origin, IntPtr err, IntPtr pv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int    FciDelete(IntPtr name, IntPtr err, IntPtr pv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool   FciTempFile(IntPtr buffer, int size, IntPtr pv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool   FciNextCabinet(IntPtr ccab, uint previousSize, IntPtr pv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int    FciStatus(uint type, uint cb1, uint cb2, IntPtr pv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FciOpenInfo(IntPtr name, IntPtr date, IntPtr time, IntPtr attributes, IntPtr err, IntPtr pv);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FDICreate(Alloc alloc, Free free, FdiOpen open, FdiRead read, FdiRead write, FdiClose close, FdiSeek seek, int cpuType, IntPtr erf);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern bool FDICopy(IntPtr fdi, string cabinetName, string cabinetPath, int flags, FdiNotify notify, IntPtr decrypt, IntPtr user);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool FDIDestroy(IntPtr fdi);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FCICreate(IntPtr erf, FciFilePlaced placed, Alloc alloc, Free free, FciOpen open, FciRead read, FciRead write, FciClose close,
                                           FciSeek seek, FciDelete delete, FciTempFile temp, ref Ccab ccab, IntPtr pv);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool FCIAddFile(IntPtr fci, byte[] sourceFile, byte[] nameInCabinet, bool execute, FciNextCabinet next, FciStatus status,
                                          FciOpenInfo openInfo, int compression);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool FCIFlushCabinet(IntPtr fci, bool getNextCabinet, FciNextCabinet next, FciStatus status);

    [DllImport("cabinet.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool FCIDestroy(IntPtr fci);
}
