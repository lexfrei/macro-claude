using System;
using System.IO;

namespace Loupedeck.MacroClaudePlugin.Status;

// Resolve a human-friendly project name from a session cwd. The default
// behaviour of Path.GetFileName(cwd) works for plain repos but falls
// apart for git worktrees: inside
//
//   /Users/you/code/myrepo-wt/feature-branch
//
// Path.GetFileName gives "feature-branch", which is a branch name, not
// a project name. The user sees fifteen buttons all labelled with
// different branch names and has no idea which repo each one belongs
// to. Resolving the main worktree path and taking its basename gives
// "myrepo" — the name the user actually thinks of.
//
// Detection is filesystem-only, no git process spawn:
//   * cwd/.git is a directory → plain repo, return basename(cwd)
//   * cwd/.git is a file      → worktree. First line is
//                                "gitdir: <path>/.git/worktrees/<name>"
//                                where <path> is the main worktree.
//                                Return basename(<path>).
//   * neither                 → not a repo, return basename(cwd)
internal static class GitRepoResolver
{
    public static String? ResolveRepoName(String? cwd)
    {
        if (String.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        var trimmed = cwd.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return null;
        }

        var gitMarker = Path.Combine(trimmed, ".git");

        try
        {
            if (Directory.Exists(gitMarker))
            {
                return Basename(trimmed);
            }

            if (File.Exists(gitMarker))
            {
                var mainPath = ReadWorktreeMainPath(gitMarker);
                if (!String.IsNullOrEmpty(mainPath))
                {
                    return Basename(mainPath);
                }
            }
        }
        catch (IOException)
        {
            // Fall through to cwd basename.
        }
        catch (UnauthorizedAccessException)
        {
            // Fall through to cwd basename.
        }

        return Basename(trimmed);
    }

    // Parse a `.git` file produced by `git worktree add`. Format:
    //
    //   gitdir: /absolute/path/to/main/.git/worktrees/<name>
    //
    // Returns "/absolute/path/to/main" — the main worktree root — or
    // null if the file does not match the expected format.
    private static String? ReadWorktreeMainPath(String gitFilePath)
    {
        String content;
        try
        {
            content = File.ReadAllText(gitFilePath).Trim();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        const String prefix = "gitdir:";
        if (!content.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var gitDir = content[prefix.Length..].Trim();
        if (String.IsNullOrEmpty(gitDir))
        {
            return null;
        }

        // Look for "/.git/worktrees/<name>" suffix. The main worktree
        // path is the substring before that marker.
        const String marker = "/.git/worktrees/";
        var idx = gitDir.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        return gitDir[..idx];
    }

    private static String? Basename(String path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return null;
        }
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1
            ? trimmed[(slash + 1)..]
            : trimmed;
    }
}
