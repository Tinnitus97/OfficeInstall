using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace OfficeInstall.Services;

/// <summary>
/// Prueft die digitale Signatur (Authenticode) einer heruntergeladenen Datei.
///
/// WARUM:
/// Das Deployment Tool wird geladen und anschliessend mit Administratorrechten
/// ausgefuehrt - erst das Paket zum Entpacken, spaeter die setup.exe bei jeder
/// Installation. Eine https-Verbindung sagt nur, dass die Datei unterwegs
/// nicht veraendert wurde, nicht, WER sie gemacht hat. Deshalb muss jede
/// dieser Dateien gueltig von Microsoft signiert sein, bevor sie startet.
///
/// Geprueft wird zweierlei:
///   1. WinVerifyTrust: Signatur unversehrt und von einer vertrauenswuerdigen
///      Stelle ausgestellt (wie der Explorer unter "Digitale Signaturen").
///   2. Der Unterzeichner ist "Microsoft Corporation".
///
/// Ausserhalb von Windows gibt es weder WinVerifyTrust noch etwas, das die
/// Datei ausfuehren koennte - dort wird nichts geprueft.
/// </summary>
public static class Authenticode
{
    public const string MicrosoftSigner = "Microsoft Corporation";

    /// <summary>
    /// null, wenn die Datei gueltig von Microsoft signiert ist (oder ausserhalb
    /// von Windows nicht geprueft werden kann), sonst der Grund.
    /// </summary>
    public static string? VerifyMicrosoft(string file)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!File.Exists(file)) return $"Datei fehlt: {file}";

        int trust;
        try
        {
            trust = VerifyTrust(file);
        }
        catch (Exception ex)
        {
            // Auch ein technischer Fehlschlag ist ein Urteil "nicht bestaetigt" -
            // mit Begruendung, damit der Aufrufer nachfragen kann, statt mit
            // einer nackten Ausnahme zu enden.
            return $"Die Signatur von {Path.GetFileName(file)} ließ sich nicht prüfen: {ex.Message}";
        }

        if (trust != 0)
            return $"{Path.GetFileName(file)} ist nicht gültig signiert (Code 0x{trust:X8}).";

        string? subject;
        try
        {
            // Gemeint ist die Signatur IN der Datei, nicht eine Zertifikatsdatei -
            // dafuer bietet X509CertificateLoader keinen Ersatz.
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            subject = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            var organization = OrganizationOf(certificate.Subject);
            if (string.Equals(organization, MicrosoftSigner, StringComparison.Ordinal)) return null;
            subject = organization ?? subject;
        }
        catch (Exception ex)
        {
            return $"Der Unterzeichner von {Path.GetFileName(file)} ließ sich nicht lesen: {ex.Message}";
        }

        return $"{Path.GetFileName(file)} ist nicht von Microsoft signiert, sondern von \"{subject}\".";
    }

    /// <summary>Liest "O=..." aus einem Zertifikatsnamen. Ohne Windows testbar.</summary>
    public static string? OrganizationOf(string distinguishedName)
    {
        foreach (var part in SplitDistinguishedName(distinguishedName))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("O=", StringComparison.OrdinalIgnoreCase))
                return trimmed[2..].Trim().Trim('"');
        }
        return null;
    }

    // Kommas in Anfuehrungszeichen gehoeren zum Wert ("O="Firma, Inc."").
    private static System.Collections.Generic.IEnumerable<string> SplitDistinguishedName(string name)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '"') quoted = !quoted;
            else if (name[i] == ',' && !quoted)
            {
                yield return name[start..i];
                start = i + 1;
            }
        }
        yield return name[start..];
    }

    // ------------------------------------------------------------ WinVerifyTrust

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    // Nichts aus dem Netz nachladen (Sperrlisten, Zwischenzertifikate): Die
    // Pruefung laeuft vor JEDEM Start der setup.exe, also auch ohne Internet -
    // dort darf sie nicht in eine Zeitueberschreitung laufen.
    private const uint WtdRevocationCheckNone = 0x00000010;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    private static readonly IntPtr InvalidHandle = new(-1);   // "kein Benutzer am Bildschirm"

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
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

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [In] ref Guid action, [In] ref WinTrustData data);

    private static int VerifyTrust(string file)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = Path.GetFullPath(file)
        };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = filePointer,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval
            };
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(InvalidHandle, ref action, ref data);

            data.dwStateAction = WtdStateActionClose;
            WinVerifyTrust(InvalidHandle, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }
}
