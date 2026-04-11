using System;

namespace Loupedeck.MacroClaudePlugin.Focus;

// Percent-encodes absolute POSIX filesystem paths for inclusion in
// vscode://file/<path> URLs. Split out of VSCodeUrlActivator so the
// encoding rules can be unit-tested without linking in the whole
// activator (which spawns /usr/bin/open and logs via PluginLog).
internal static class VSCodeUrlEncoder
{
    // Percent-encode an absolute POSIX path so it can be safely
    // concatenated into a vscode://file URL. Each non-empty segment
    // is Uri.EscapeDataString'd, segments are rejoined with literal
    // '/', and a leading '/' is preserved. Empty segments (runs of
    // '/') collapse to nothing because that matches what
    // LaunchServices and VS Code's URI parser actually accept.
    //
    // Reserved characters that otherwise break the URL — most
    // notably '#' (fragment), '?' (query), '%' (escape) — end up
    // percent-encoded. Ordinary unreserved characters (letters,
    // digits, '-', '_', '.', '~') pass through unchanged.
    public static String EncodePath(String absolutePath)
    {
        if (String.IsNullOrEmpty(absolutePath))
        {
            return "/";
        }

        var segments = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "/";
        }

        var encoded = new String[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            encoded[i] = Uri.EscapeDataString(segments[i]);
        }
        return "/" + String.Join('/', encoded);
    }
}
