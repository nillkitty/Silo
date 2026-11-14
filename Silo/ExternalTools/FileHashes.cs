using System.Security.Cryptography;
using Silo.Tasks;

namespace Silo.ExternalTools;

/// <summary>The checksums the Copy Hash tools put on the clipboard.</summary>
public enum FileHashKind
{
    Md5,
    Crc32,
    Sha1,
    Sha256,
    Sha512
}

/// <summary>Computes a file's checksum (as lower-case hex) and copies it, as a task the Tasks pane shows while a large file is read.</summary>
public static class FileHashes
{
    /// <summary>The name of a checksum, as people write it ("SHA-256").</summary>
    public static string NameOf(FileHashKind kind) => kind switch
                                                      {
                                                          FileHashKind.Md5    => "MD5",
                                                          FileHashKind.Crc32  => "CRC32",
                                                          FileHashKind.Sha1   => "SHA-1",
                                                          FileHashKind.Sha256 => "SHA-256",
                                                          _                   => "SHA-512"
                                                      };

    /// <summary>The checksum of the contents of <paramref name="stream" />, as lower-case hex.</summary>
    public static async Task<string> ComputeAsync(Stream stream, FileHashKind kind, CancellationToken cancel = default)
    {
        byte[] hash = kind switch
                      {
                          FileHashKind.Md5    => await MD5.HashDataAsync(stream, cancel),
                          FileHashKind.Sha1   => await SHA1.HashDataAsync(stream, cancel),
                          FileHashKind.Sha256 => await SHA256.HashDataAsync(stream, cancel),
                          FileHashKind.Sha512 => await SHA512.HashDataAsync(stream, cancel),
                          _                   => await Crc32Async(stream, cancel)
                      };
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>The checksum of the file at <paramref name="path" />, as lower-case hex.</summary>
    public static async Task<string> ComputeAsync(string path, FileHashKind kind, CancellationToken cancel = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
                                                FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeAsync(stream, kind, cancel);
    }

    /// <summary>Computes the checksum of <paramref name="file" /> as a task and puts it on the clipboard.</summary>
    public static Task CopyAsync(FileInfo file, FileHashKind kind)
    {
        string name = NameOf(kind);
        return FileTasks.RunAsync($"Computing the {name} of {file.Name}", "External tools", async task =>
                                  {
                                      string hash = await ComputeAsync(file.FullName, kind, task.Token).ConfigureAwait(false);
                                      task.Report(hash);
                                      Application.Current.Dispatcher.Invoke(() =>
                                                                            {
                                                                                Clipboard.SetText(hash);
                                                                                App.Status($"Copied the {name} of {file.Name}: {hash}");
                                                                            });
                                  }, true);
    }

    // ---- CRC-32 (IEEE 802.3, as zip and PNG use it) ---------------------------------------------------------------

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }

    private static async Task<byte[]> Crc32Async(Stream stream, CancellationToken cancel)
    {
        uint crc    = 0xFFFFFFFFu;
        var  buffer = new byte[1 << 16];
        int  read;
        while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
            for (int i = 0; i < read; i++)
                crc = CrcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);

        crc ^= 0xFFFFFFFFu;
        // big-endian, so the hex reads as the number ("cbf43926" for "123456789")
        return [(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }
}
