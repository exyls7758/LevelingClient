using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Multiverse.Services
{
    internal static class AudioApplicationPolicy
    {
        internal static readonly string[] DefaultCommunicationExecutables =
        {
            "discord",
            "discordcanary",
            "discordptb",
            "skype",
            "skypeapp",
            "ms-teams",
            "teams",
            "msteams",
            "teamspeak",
            "ts3client_win64",
            "ts3client_win32",
            "zoom",
            "zoommeeting",
            "webex",
            "webexmta"
        };

        internal static string NormalizeExecutable(string? value)
        {
            string normalized = Path.GetFileNameWithoutExtension(value ?? "")
                .Trim()
                .ToLowerInvariant();
            return normalized;
        }

        internal static bool IsExcluded(
            string? executable,
            IReadOnlyCollection<string> exclusions)
        {
            string normalized = NormalizeExecutable(executable);
            if (string.IsNullOrWhiteSpace(normalized))
                return true;

            foreach (string entry in exclusions)
            {
                if (string.Equals(
                    NormalizeExecutable(entry),
                    normalized,
                    StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        internal static IReadOnlyList<string> MergeWithDefaultCommunicationExclusions(
            IEnumerable<string>? exclusions)
        {
            IEnumerable<string> custom = exclusions ?? Array.Empty<string>();
            return NormalizeExclusions(DefaultCommunicationExecutables.Concat(custom));
        }

        internal static IReadOnlyList<string> NormalizeExclusions(
            IEnumerable<string>? exclusions)
        {
            if (exclusions is null)
                return Array.Empty<string>();

            return exclusions
                .Select(NormalizeExecutable)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
