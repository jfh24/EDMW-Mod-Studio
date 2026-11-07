using System.Text.Json;

namespace EDMWMapDesigner.Networking;

internal enum OnlineGameSessionFrameType : byte
{
    LaunchClient = 4,
    GameReady = 5,
    LaunchFailed = 6,
}

internal sealed record OnlineGameSessionStatus(Guid SessionId, string Message);
internal sealed record OnlineGameClientLaunch(Guid SessionId, string HostVirtualIpv4);

/// <summary>Small bounded control frames for coordinating game network setup over authenticated room ICE channels.</summary>
internal static class OnlineGameSessionProtocol
{
    private const int HeaderBytes = 5;

    public static byte[] EncodeJson<T>(OnlineGameSessionFrameType type, T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, OnlineRelayClient.JsonOptions);
        if (json.Length + HeaderBytes > OnlineGameIceSession.MaximumControlFrameBytes)
            throw new InvalidDataException("The game network control message is too large.");
        var frame = new byte[HeaderBytes + json.Length];
        "EDMS"u8.CopyTo(frame);
        frame[4] = (byte)type;
        json.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    public static bool TryDecodeJson<T>(ReadOnlySpan<byte> frame, OnlineGameSessionFrameType expectedType, out T? value)
    {
        value = default;
        if (!HasHeader(frame, expectedType) || frame.Length <= HeaderBytes) return false;
        try
        {
            value = JsonSerializer.Deserialize<T>(frame[HeaderBytes..], OnlineRelayClient.JsonOptions);
            return value is not null;
        }
        catch (JsonException) { return false; }
    }

    public static bool HasHeader(ReadOnlySpan<byte> frame, OnlineGameSessionFrameType type) =>
        frame.Length >= HeaderBytes && frame[..4].SequenceEqual("EDMS"u8) && frame[4] == (byte)type;
}
