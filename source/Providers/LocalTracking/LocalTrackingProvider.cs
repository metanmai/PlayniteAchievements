using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;

namespace PlayniteAchievements.Providers.LocalTracking
{
    internal sealed class LocalTrackingSettings : ProviderSettingsBase
    {
        public override string ProviderKey => LocalAchievementProfile.ProviderKey;
    }

    internal sealed class LocalTrackingProvider : DataProviderBase<LocalTrackingSettings>, IDataProvider,
        IProviderOverride, IInGameProgressSource
    {
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;

        public LocalTrackingProvider(ILogger logger, IPlayniteAPI playniteApi)
        {
            _logger = logger;
            _api = playniteApi;
        }

        public string ProviderName => ProviderRegistry.GetLocalizedName(ProviderKey);
        public string ProviderKey => LocalAchievementProfile.ProviderKey;
        public string ProviderIconKey => "ProviderIconManual";
        public string ProviderColorHex => "#287B7B";
        public bool IsAuthenticated => true;
        public ISessionManager AuthSession => null;
        public IFriendsProvider Friends => null;

        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_LocalTracking_ProfilePath",
            raw =>
            {
                var path = raw?.Trim().Trim('"');
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                    ? ProviderOverrideValidation.Valid(path)
                    : ProviderOverrideValidation.Invalid("LOCPlayAch_InvalidPath");
            });

        public bool IsCapable(Game game) =>
            game != null && GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, ProviderKey, out _);

        public ProviderSettingsViewBase CreateSettingsView() => new LocalTrackingSettingsView();

        internal LocalAchievementProfile ReadProfile(Game game, string path) =>
            LocalAchievementProfile.Load(path, value => PathExpansion.ExpandGamePath(_api, game, value));

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                (game, token) =>
                {
                    if (!GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, ProviderKey, out var path))
                    {
                        return Task.FromResult(ProviderRefreshExecutor.ProviderGameResult.Skipped());
                    }

                    var profile = ReadProfile(game, path);
                    var unlocked = profile.ReadUnlocks(token);
                    var achievements = profile.Rules.Select(rule =>
                    {
                        var earned = unlocked.TryGetValue(rule.Id, out var time);
                        return new AchievementDetail
                        {
                            ApiName = rule.Id,
                            DisplayName = rule.Name,
                            Description = rule.Description,
                            Hidden = rule.Hidden,
                            UnlockedIconPath = rule.IconPath,
                            Unlocked = earned,
                            UnlockTimeUtc = time
                        };
                    }).ToList();

                    return Task.FromResult(new ProviderRefreshExecutor.ProviderGameResult
                    {
                        Data = new GameAchievementData
                        {
                            ProviderKey = ProviderKey,
                            ProviderGameKey = game.Id.ToString("D"),
                            GameName = game.Name,
                            PlayniteGameId = game.Id,
                            LibrarySourceName = game.Source?.Name,
                            RaritySource = RaritySource.None,
                            Achievements = achievements,
                            LastUpdatedUtc = DateTime.UtcNow
                        }
                    });
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, _) => _logger?.Warn(ex, $"Could not read the local achievement files for '{game?.Name}'."),
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel);
        }

        InGameProgressRegistration IInGameProgressSource.TryRegister(Game game, GameAchievementData cachedSchema)
        {
            if (!string.Equals(cachedSchema?.ProviderKey, ProviderKey, StringComparison.OrdinalIgnoreCase) ||
                !(cachedSchema.Achievements?.Count > 0) || game == null ||
                !GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, ProviderKey, out var path))
            {
                return null;
            }

            var profile = ReadProfile(game, path);
            return new InGameProgressRegistration
            {
                ProviderKey = ProviderKey,
                WatchTargets = new[] { profile.ProgressPath },
                PollInterval = InGameProgressRegistration.FileWatchSafetyPollInterval,
                UnlockAnchorPolicy = InGameUnlockAnchorPolicy.SourceObservation,
                State = profile
            };
        }

        Task<IReadOnlyList<InGameProgressQueryResult>> IInGameProgressSource.QueryAsync(
            IReadOnlyList<InGameTrackingContext> games,
            CancellationToken cancellationToken)
        {
            var results = new List<InGameProgressQueryResult>(games.Count);
            foreach (var context in games)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gameId = context.Game.Id;
                var profile = (LocalAchievementProfile)context.Registration.State;
                try
                {
                    var observations = profile.ReadUnlocks(cancellationToken)
                        .Select(unlock => new AchievementProgressObservation
                        {
                            ApiName = unlock.Key,
                            Unlocked = true,
                            UnlockTimeUtc = unlock.Value
                        }).ToList();
                    results.Add(InGameProgressQueryResult.Succeeded(gameId, observations));
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException ||
                                           ex is JsonException || ex is System.Text.DecoderFallbackException)
                {
                    results.Add(InGameProgressQueryResult.Failed(gameId, ex.GetType().Name));
                }
            }

            return Task.FromResult<IReadOnlyList<InGameProgressQueryResult>>(results);
        }

        private sealed class LocalTrackingSettingsView : ProviderSettingsViewBase
        {
            public LocalTrackingSettingsView()
            {
                Content = new TextBlock
                {
                    Text = ResourceProvider.GetString("LOCPlayAch_LocalTracking_SettingsHint"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                };
            }
        }
    }
}
