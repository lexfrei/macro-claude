using System;
using System.Text;

namespace Loupedeck.MacroClaudePlugin.Status;

// Derive the per-project directory name under ~/.claude/projects from
// a session cwd. Claude Code itself uses a stable convention that we
// piggy-back on to look up session transcripts in O(1) instead of
// recursively scanning every JSONL file in ~/.claude/projects.
//
// The rule, verified against the real ~/.claude/projects directory on
// disk: every character outside [A-Za-z0-9_-] is replaced with '-'.
// Dashes and underscores already in the cwd survive unchanged, and
// consecutive specials collapse to consecutive dashes (e.g. "/." at
// the start of ".claude-worktrees" becomes "--").
//
// If Claude Code ever changes this convention, StatusReader falls
// back to a one-shot recursive lookup and caches the result, so a
// mismatch here degrades performance for the affected session but
// never breaks correctness.
public static class TranscriptPathEncoder
{
    public static String Encode(String cwd)
    {
        if (String.IsNullOrEmpty(cwd))
        {
            return String.Empty;
        }

        var sb = new StringBuilder(cwd.Length);
        foreach (var c in cwd)
        {
            if (IsPreserved(c))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('-');
            }
        }
        return sb.ToString();
    }

    private static Boolean IsPreserved(Char c)
        => c is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z')
            or (>= '0' and <= '9')
            or '-'
            or '_';
}
