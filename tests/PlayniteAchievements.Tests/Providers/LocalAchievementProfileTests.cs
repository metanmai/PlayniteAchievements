using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.LocalTracking;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.InGameMonitoring;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class LocalAchievementProfileTests
    {
        private const string ProfileJson = @"{
            ""version"":1,
            ""progressFile"":""progress.json"",
            ""achievements"":[
                {""id"":""first"", ""name"":""First Victory"", ""pointer"":""/players/0/won"",
                 ""unlockTimePointer"":""/players/0/wonAt""},
                {""id"":""second"", ""name"":""High Score"", ""pointer"":""/stats/a~1b~0c"", ""equals"":10},
                {""id"":""third"", ""name"":""Complete"", ""pointer"":""/status"", ""equals"":""complete""}
            ]
        }";

        [TestMethod]
        [DoNotParallelize]
        public void LinkedGameRefreshesDefinitionsAndReadsLiveProgress()
        {
            WithWorkspace((profilePath, progressPath) =>
            {
                File.WriteAllText(profilePath, ProfileJson);
                var previousPlugin = PlayniteAchievementsPlugin.Instance;
                var store = new GameCustomDataStore(Path.GetDirectoryName(profilePath));
                PlayniteAchievementsPlugin.Instance = new PlayniteAchievementsPlugin { GameCustomDataStore = store };
                try
                {
                    var game = new Game("Local game") { Id = Guid.NewGuid() };
                    var provider = new LocalTrackingProvider(null, null);
                    Assert.IsFalse(provider.IsCapable(game));
                    store.Update(game.Id, data => data.ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = provider.ProviderKey,
                        Value = profilePath
                    });
                    Assert.IsTrue(provider.IsCapable(game));

                    GameAchievementData schema = null;
                    var refresh = provider.RefreshAsync(new[] { game }, null, (_, data) =>
                    {
                        schema = data;
                        return Task.CompletedTask;
                    }, CancellationToken.None).GetAwaiter().GetResult();
                    Assert.AreEqual(1, refresh.Summary.GamesWithAchievements);
                    Assert.AreEqual(3, schema.Achievements.Count);
                    Assert.IsFalse(schema.Achievements.Any(achievement => achievement.Unlocked));

                    var source = (IInGameProgressSource)provider;
                    var registration = source.TryRegister(game, schema);
                    CollectionAssert.AreEqual(new[] { progressPath }, registration.WatchTargets.ToArray());
                    var context = new[] { new InGameTrackingContext { Game = game, CachedSchema = schema, Registration = registration } };
                    Assert.AreEqual(0, source.QueryAsync(context, CancellationToken.None).Result.Single().Achievements.Count);

                    File.WriteAllText(progressPath, @"{""status"":");
                    Assert.IsFalse(source.QueryAsync(context, CancellationToken.None).Result.Single().Success);

                    File.WriteAllText(progressPath, @"{""status"":""complete""}");
                    var unlock = source.QueryAsync(context, CancellationToken.None).Result.Single().Achievements.Single();
                    Assert.AreEqual("third", unlock.ApiName);
                    Assert.IsTrue(unlock.Unlocked);
                }
                finally
                {
                    PlayniteAchievementsPlugin.Instance = previousPlugin;
                }
            });
        }

        [TestMethod]
        public void ReadsAnExistingUnlockAndDetectsNewRulesAfterProgressChanges()
        {
            WithWorkspace((profilePath, progressPath) =>
            {
                File.WriteAllText(profilePath, ProfileJson);
                File.WriteAllText(progressPath, @"{
                    ""players"":[{""won"":true, ""wonAt"":""2026-10-15T12:30:00+02:00""}],
                    ""stats"":{""a/b~c"":""10""}
                }");

                var profile = LocalAchievementProfile.Load(profilePath, null);
                Assert.AreEqual(progressPath, profile.ProgressPath);
                Assert.AreEqual("First Victory", profile.Rules[0].Name);

                var baseline = profile.ReadUnlocks(CancellationToken.None);
                Assert.AreEqual(1, baseline.Count);
                Assert.AreEqual(new DateTime(2026, 10, 15, 10, 30, 0, DateTimeKind.Utc), baseline["first"]);
                Assert.IsFalse(InGameUnlockEmissionPolicy.ShouldEmit(false, DateTime.UtcNow, baseline["first"]));

                File.WriteAllText(progressPath, @"{""stats"":{""a/b~c"":10}, ""status"":""complete""}");
                var changed = profile.ReadUnlocks(CancellationToken.None);
                CollectionAssert.AreEquivalent(new[] { "second", "third" }, changed.Keys.ToArray());
                Assert.IsTrue(InGameUnlockEmissionPolicy.ShouldEmit(true, DateTime.UtcNow, changed["second"]));
            });
        }

        [TestMethod]
        public void ReadsAFileCreatedLaterAndRecoversAfterAnIncompleteWrite()
        {
            WithWorkspace((profilePath, progressPath) =>
            {
                File.WriteAllText(profilePath, ProfileJson);
                var profile = LocalAchievementProfile.Load(profilePath, null);
                Assert.AreEqual(0, profile.ReadUnlocks(CancellationToken.None).Count);

                File.WriteAllText(progressPath, @"{""status"":");
                Assert.ThrowsException<JsonReaderException>(() => profile.ReadUnlocks(CancellationToken.None));

                File.WriteAllText(progressPath, @"{""status"":""complete""}");
                Assert.IsTrue(profile.ReadUnlocks(CancellationToken.None).ContainsKey("third"));
            });
        }

        [DataTestMethod]
        [DataRow("""{"version":2,"progressFile":"progress.json","achievements":[{}]}""")]
        [DataRow("""{"version":1,"progressFile":"profile.json","achievements":[{}]}""")]
        [DataRow("""{"version":1,"progressFile":"progress.json","achievements":[{"id":"a","name":"A","pointer":"/bad~2escape"}]}""")]
        [DataRow("""{"version":1,"progressFile":"progress.json","achievements":[{"id":"a","name":"A","pointer":"/a"},{"id":"A","name":"B","pointer":"/b"}]}""")]
        public void RejectsInvalidProfiles(string json)
        {
            WithWorkspace((profilePath, _) =>
            {
                File.WriteAllText(profilePath, json);
                Assert.ThrowsException<InvalidDataException>(() => LocalAchievementProfile.Load(profilePath, null));
            });
        }

        private static void WithWorkspace(Action<string, string> test)
        {
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            var directory = Path.Combine(parent, "pa-local-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                test(Path.Combine(directory, "profile.json"), Path.Combine(directory, "progress.json"));
            }
            finally
            {
                var resolved = new DirectoryInfo(directory).FullName;
                if (resolved == directory && Path.GetDirectoryName(resolved) == parent)
                {
                    Directory.Delete(resolved, recursive: true);
                }
            }
        }
    }
}
