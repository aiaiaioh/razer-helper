using System.Buffers.Binary;

namespace RazerHelper.Core.Services;

/// <summary>
/// The little of razer-daemon's local protocol this app needs: a way to ask it
/// to save its state and exit. The daemon listens on a loopback TCP port and
/// reads commands written with Rust's bincode.
/// </summary>
internal static class RazerDaemonIpc
{
    public const string Address = "127.0.0.1";
    public const int Port = 29494;

    /// <summary>
    /// bincode writes a variant with no fields as its position in the enum, as a
    /// 32-bit little-endian number. <c>Shutdown</c> is the last of the 28
    /// variants of <c>DaemonCommand</c> in razer-control-win's <c>src/comms.rs</c>
    /// (counting from 0), so if a command is ever added after it, or the order
    /// changes, this number must follow.
    /// </summary>
    public const uint ShutdownVariantIndex = 27;

    /// <summary>The bytes that make the daemon save its effects and exit. It sends no reply.</summary>
    public static byte[] ShutdownCommand()
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, ShutdownVariantIndex);
        return bytes;
    }
}
