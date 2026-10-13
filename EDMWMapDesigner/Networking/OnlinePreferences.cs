using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EDMWMapDesigner.Networking;

/// <summary>Remembers relay profiles, protecting each device credential for the current Windows user.</summary>
public sealed class OnlinePreferences
{
    public const string DefaultEndpoint = "https://edmw-mod-studio.onrender.com";
    private const int MaximumPreferencesBytes = 4 * 1024 * 1024;
    public string Endpoint { get; set; } = DefaultEndpoint;
    public string DisplayName { get; set; } = Environment.UserName;
    public Dictionary<string, string> ProtectedIdentities { get; set; } = new(StringComparer.Ordinal);
    [JsonIgnore] public bool PersistIdentity { get; set; } = true;
    [JsonIgnore] internal string? LoadError { get; private init; }

    public static OnlinePreferences Load()
    {
        try
        {
            if (!File.Exists(PreferencePath)) return new();
            if (new FileInfo(PreferencePath).Length > MaximumPreferencesBytes)
                throw new InvalidDataException("The saved online profiles exceed the supported size limit.");
            var preferences = JsonSerializer.Deserialize<OnlinePreferences>(File.ReadAllText(PreferencePath)) ?? new();
            if (string.IsNullOrWhiteSpace(preferences.Endpoint)) preferences.Endpoint = DefaultEndpoint;
            preferences.ProtectedIdentities ??= new(StringComparer.Ordinal);
            return preferences;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return new() { LoadError = "The saved online profiles could not be read. Restore or reset online.json before connecting: " + error.Message }; }
    }

    public void Save()
    {
        if (!PersistIdentity) return;
        var json = JsonSerializer.SerializeToUtf8Bytes(this);
        if (json.Length > MaximumPreferencesBytes)
            throw new InvalidDataException("The saved online profiles exceed the supported size limit.");
        var path = PreferencePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, json);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal OnlineIdentity? GetIdentity(string endpoint)
    {
        if (LoadError is not null) throw new InvalidDataException(LoadError);
        if (!ProtectedIdentities.TryGetValue(endpoint, out var protectedIdentity)) return null;
        if (protectedIdentity.Length > 1024 * 1024)
            throw new InvalidDataException("The saved online device credential exceeds the supported size limit.");
        try
        {
            var bytes = Unprotect(Convert.FromBase64String(protectedIdentity), endpoint);
            try
            {
                var identity = JsonSerializer.Deserialize<OnlineIdentity>(bytes);
                if (identity is null || identity.UserId == Guid.Empty || identity.Token is null || identity.Token.Length is < 32 or > 512)
                    throw new InvalidDataException("The saved online device credential is invalid. Reconnect with a valid relay credential.");
                return identity with { FriendReceipts = OnlineCredentialLimits.ValidateReceipts(identity.FriendReceipts) };
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception error) when (error is FormatException or JsonException or Win32Exception)
        { throw new InvalidDataException("The saved online credential could not be unlocked by this Windows user.", error); }
    }

    internal void SetIdentity(string endpoint, OnlineIdentity identity)
    {
        if (!PersistIdentity) return;
        var validated = identity with { FriendReceipts = OnlineCredentialLimits.ValidateReceipts(identity.FriendReceipts) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(validated);
        try
        {
            var previousExists = ProtectedIdentities.TryGetValue(endpoint, out var previous);
            ProtectedIdentities[endpoint] = Convert.ToBase64String(Protect(bytes, endpoint));
            try { Save(); }
            catch
            {
                if (previousExists) ProtectedIdentities[endpoint] = previous!;
                else ProtectedIdentities.Remove(endpoint);
                throw;
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Protect(byte[] bytes, string endpoint) => Transform(bytes, endpoint, true);
    private static byte[] Unprotect(byte[] bytes, string endpoint) => Transform(bytes, endpoint, false);

    private static byte[] Transform(byte[] bytes, string endpoint, bool protect)
    {
        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes("EDMW online relay v1|" + endpoint));
        var input = new DataBlob { Length = bytes.Length, Pointer = Marshal.AllocHGlobal(bytes.Length) };
        var salt = new DataBlob { Length = entropy.Length, Pointer = Marshal.AllocHGlobal(entropy.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Pointer, bytes.Length);
            Marshal.Copy(entropy, 0, salt.Pointer, entropy.Length);
            // CRYPTPROTECT_UI_FORBIDDEN; user scope deliberately excludes LOCAL_MACHINE.
            var succeeded = protect
                ? CryptProtectData(ref input, null, ref salt, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref salt, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Pointer, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, input.Pointer, bytes.Length);
            Marshal.FreeHGlobal(input.Pointer);
            Marshal.FreeHGlobal(salt.Pointer);
            if (output.Pointer != IntPtr.Zero) LocalFree(output.Pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Pointer; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    private static string PreferencePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EDMW Map Designer", "online.json");
}
