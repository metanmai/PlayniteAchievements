using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace PlayniteAchievements.Providers.LocalTracking
{
    internal sealed class LocalAchievementProfile
    {
        internal const string ProviderKey = "LocalTracking";

        private const long MaxProfileBytes = 1024 * 1024;
        private const long MaxProgressBytes = 8 * 1024 * 1024;

        private LocalAchievementProfile(string progressPath, IReadOnlyList<LocalAchievementRule> rules)
        {
            ProgressPath = progressPath;
            Rules = rules;
        }

        public string ProgressPath { get; }

        public IReadOnlyList<LocalAchievementRule> Rules { get; }

        public static LocalAchievementProfile Load(string path, Func<string, string> expandPath)
        {
            path = ResolvePath(path, null, expandPath);
            var document = ReadJson(path, MaxProfileBytes);
            if (document.Type != JTokenType.Object ||
                !JToken.DeepEquals(document["version"], new JValue(1)) ||
                !(document["achievements"] is JArray definitions) ||
                definitions.Count == 0)
            {
                throw new InvalidDataException("Expected a version 1 profile with a nonempty achievements array.");
            }

            var directory = Path.GetDirectoryName(path);
            var progressPath = ResolvePath(RequiredString(document, "progressFile"), directory, expandPath);
            if (string.Equals(path, progressPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Choose a progress file separate from the profile.");
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rules = new List<LocalAchievementRule>(definitions.Count);
            foreach (var definition in definitions)
            {
                var id = RequiredString(definition, "id");
                if (!ids.Add(id))
                {
                    throw new InvalidDataException($"Achievement ID '{id}' appears more than once.");
                }

                var expected = definition["equals"]?.DeepClone() ?? new JValue(true);
                if (!IsMatchValue(expected))
                {
                    throw new InvalidDataException($"Achievement '{id}' needs a boolean, number or string for 'equals'.");
                }

                var timestamp = OptionalString(definition, "unlockTimePointer", trim: false);
                var icon = OptionalString(definition, "icon");
                var hidden = definition["hidden"];
                if (hidden != null && hidden.Type != JTokenType.Boolean)
                {
                    throw new InvalidDataException($"Achievement '{id}' needs a boolean for 'hidden'.");
                }

                rules.Add(new LocalAchievementRule(
                    id,
                    RequiredString(definition, "name"),
                    OptionalString(definition, "description"),
                    icon == null ? null : ResolvePath(icon, directory, expandPath),
                    (bool?)hidden ?? false,
                    ParsePointer(RequiredString(definition, "pointer", trim: false)),
                    expected,
                    timestamp == null ? null : ParsePointer(timestamp)));
            }

            return new LocalAchievementProfile(progressPath, rules);
        }

        public Dictionary<string, DateTime?> ReadUnlocks(CancellationToken cancel)
        {
            var unlocked = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            JToken progress;
            try
            {
                progress = ReadJson(ProgressPath, MaxProgressBytes);
            }
            catch (FileNotFoundException)
            {
                return unlocked;
            }
            catch (DirectoryNotFoundException)
            {
                return unlocked;
            }

            if (progress.Type != JTokenType.Object && progress.Type != JTokenType.Array)
            {
                throw new InvalidDataException("Expected a JSON object or array in the progress file.");
            }

            foreach (var rule in Rules)
            {
                cancel.ThrowIfCancellationRequested();
                if (rule.IsUnlocked(progress))
                {
                    unlocked.Add(rule.Id, rule.GetUnlockTime(progress));
                }
            }

            return unlocked;
        }

        private static JToken ReadJson(string path, long maxBytes)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > maxBytes)
            {
                throw new InvalidDataException($"JSON file exceeds the {maxBytes / (1024 * 1024)} MiB limit.");
            }

            using var text = new StreamReader(file, new System.Text.UTF8Encoding(false, true));
            using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 };
            var document = JToken.ReadFrom(reader);
            if (reader.Read())
            {
                throw new InvalidDataException("Unexpected content after the JSON document.");
            }

            return document;
        }

        private static string ResolvePath(string path, string directory, Func<string, string> expandPath)
        {
            path = Environment.ExpandEnvironmentVariables(expandPath?.Invoke(path) ?? path).Trim();
            if (path.Length == 0 || path[0] == '\\' || path[0] == '/' ||
                (Path.IsPathRooted(path) && (path.Length < 3 || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))))
            {
                throw new InvalidDataException("Use a drive-qualified local path or a path relative to the profile.");
            }

            if (!Path.IsPathRooted(path))
            {
                if (directory == null)
                {
                    throw new InvalidDataException("Use a full local path for the achievement profile.");
                }

                path = Path.Combine(directory, path);
            }

            return Path.GetFullPath(path);
        }

        private static string RequiredString(JToken obj, string key, bool trim = true)
            => OptionalString(obj, key, trim) ?? throw new InvalidDataException($"'{key}' is required.");

        private static string OptionalString(JToken obj, string key, bool trim = true)
        {
            if (!(obj is JObject))
            {
                throw new InvalidDataException("Each achievement must be a JSON object.");
            }

            var token = obj[key];
            if (token == null)
            {
                return null;
            }

            if (token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token))
            {
                throw new InvalidDataException($"'{key}' must be a nonempty string.");
            }

            return trim ? ((string)token).Trim() : (string)token;
        }

        private static string[] ParsePointer(string pointer)
        {
            if (pointer[0] != '/')
            {
                throw new InvalidDataException($"JSON pointer '{pointer}' must start with '/'.");
            }

            var segments = pointer.Substring(1).Split('/');
            foreach (var segment in segments)
            {
                for (var index = segment.IndexOf('~'); index >= 0; index = segment.IndexOf('~', index + 2))
                {
                    if (index + 1 == segment.Length || (segment[index + 1] != '0' && segment[index + 1] != '1'))
                    {
                        throw new InvalidDataException($"Invalid escape in JSON pointer '{pointer}'.");
                    }
                }
            }

            return segments.Select(segment => segment.Replace("~1", "/").Replace("~0", "~")).ToArray();
        }

        private static bool IsMatchValue(JToken token)
            => token.Type == JTokenType.Boolean || token.Type == JTokenType.String ||
               token.Type == JTokenType.Integer || token.Type == JTokenType.Float;
    }

    internal sealed class LocalAchievementRule
    {
        private readonly string[] _pointer;
        private readonly JToken _expected;
        private readonly string[] _timestampPointer;

        public LocalAchievementRule(string id, string name, string description, string iconPath, bool hidden,
            string[] pointer, JToken expected, string[] timestampPointer)
        {
            Id = id;
            Name = name;
            Description = description;
            IconPath = iconPath;
            Hidden = hidden;
            _pointer = pointer;
            _expected = expected;
            _timestampPointer = timestampPointer;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string IconPath { get; }
        public bool Hidden { get; }

        public bool IsUnlocked(JToken progress) => JToken.DeepEquals(Select(progress, _pointer), _expected);

        public DateTime? GetUnlockTime(JToken progress)
        {
            var token = _timestampPointer == null ? null : Select(progress, _timestampPointer);
            if (token?.Type == JTokenType.String &&
                DateTimeOffset.TryParse((string)token, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            {
                return time.UtcDateTime;
            }

            return null;
        }

        private static JToken Select(JToken node, IEnumerable<string> segments)
        {
            foreach (var segment in segments)
            {
                if (node is JObject obj)
                {
                    node = obj[segment];
                }
                else if (node is JArray array &&
                    int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                    (segment.Length == 1 || segment[0] != '0') && index < array.Count)
                {
                    node = array[index];
                }
                else
                {
                    return null;
                }
            }

            return node;
        }
    }
}
