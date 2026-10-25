using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EasyVocal.Platform.Windows;

/// <summary>What <see cref="Authenticode.Check"/> found out about a program file.</summary>
internal enum SignatureCheck
{
    /// <summary>Signed, Windows trusts the signature, and the signer is VB-Audio.</summary>
    Ok,
    /// <summary>Not signed, altered after signing, or the signature is not trusted by Windows.</summary>
    Untrusted,
    /// <summary>Validly signed, but by somebody else.</summary>
    WrongSigner,
}

/// <summary>
/// Checks the Authenticode signature of a file before EasyVocal runs it with administrator rights: Windows must
/// trust the signature (<c>WinVerifyTrust</c>, no dialogs) and the signer must be VB-Audio / Vincent Burel.
/// </summary>
internal static class Authenticode
{
    /// <summary>
    /// The names VB-Audio's certificate may carry in its CN or O field (compared without case). Today's VB-CABLE installers
    /// are signed "CN=BUREL VINCENT Entrepreneur individuel, O=BUREL VINCENT Entrepreneur individuel, …, C=FR".
    /// </summary>
    private static readonly string[] VbAudioNames = { "vb-audio software", "vb-audio", "vb audio software", "vincent burel", "burel vincent entrepreneur individuel" };

    /// <summary>Trusted by Windows AND signed by VB-Audio.</summary>
    public static SignatureCheck Check(string path)
    {
        if (!IsTrusted(path)) return SignatureCheck.Untrusted;
        var subject = SignerSubject(path);
        return subject != null && IsVbAudioSubject(subject) ? SignatureCheck.Ok : SignatureCheck.WrongSigner;
    }

    /// <summary>
    /// True when the certificate subject (e.g. <c>CN=VB-Audio Software, O=VB-Audio Software, C=FR</c>) names VB-Audio
    /// in its common name or organisation. Whole names only: "Not VB-Audio Software" or "VB-Audio Software.evil" don't pass.
    /// </summary>
    public static bool IsVbAudioSubject(string subject)
    {
        foreach (var (key, value) in ParseSubject(subject))
        {
            if (!key.Equals("CN", StringComparison.OrdinalIgnoreCase) && !key.Equals("O", StringComparison.OrdinalIgnoreCase)) continue;
            var name = value.Trim();
            foreach (var allowed in VbAudioNames)
                if (name.Equals(allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Splits <c>CN=A, O="B, Inc", C=FR</c> into its (key, value) pairs; quotes and backslash escapes are honoured.</summary>
    public static List<(string Key, string Value)> ParseSubject(string subject)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(subject)) return result;
        var key = new System.Text.StringBuilder();
        var value = new System.Text.StringBuilder();
        bool inValue = false, quoted = false;

        void Finish()
        {
            if (inValue && key.ToString().Trim().Length > 0) result.Add((key.ToString().Trim(), value.ToString()));
            key.Clear();
            value.Clear();
            inValue = quoted = false;
        }

        for (int i = 0; i < subject.Length; i++)
        {
            char c = subject[i];
            if (!inValue)
            {
                if (c == '=') inValue = true;
                else if (c == ',' || c == ';') Finish();
                else key.Append(c);
            }
            else if (quoted)
            {
                if (c == '"' && i + 1 < subject.Length && subject[i + 1] == '"') { value.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else value.Append(c);
            }
            else if (c == '"' && value.ToString().Trim().Length == 0) quoted = true;
            else if (c == '\\' && i + 1 < subject.Length) value.Append(subject[++i]);
            else if (c == ',' || c == ';') Finish();
            else value.Append(c);
        }
        Finish();
        return result;
    }

    /// <summary>The subject of the certificate that signed the file, or null when it has no readable signature.</summary>
    public static string? SignerSubject(string path)
    {
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(path);
            return cert.Subject;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // ---------- WinVerifyTrust ----------

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);

    /// <summary>
    /// True when the file carries an embedded Authenticode signature that Windows trusts (valid, untampered,
    /// chains to a trusted root). No dialogs. Revocation lists are not asked for: that needs the network and would
    /// fail offline or behind a proxy; the signer name and the official https address are checked as well.
    /// </summary>
    public static bool IsTrusted(string path)
    {
        var filePath = Marshal.StringToHGlobalUni(path);
        var fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = filePath }, fileInfo, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfo,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_NONE,
            };
            var invalidHandle = new IntPtr(-1); // "no window": Windows must not show any dialog
            int result;
            try
            {
                result = WinVerifyTrust(invalidHandle, GenericVerifyV2, ref data);
            }
            finally
            {
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(invalidHandle, GenericVerifyV2, ref data);
            }
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(filePath);
        }
    }
}
