using System.Reflection;
using TomodachiDrawer.Core;
using TomodachiDrawer.Core.ImageProcessing.Quantizers;
using TomodachiDrawer.Core.Models;
using Xunit;

namespace TomodachiDrawer.Core.Tests
{
    /// <summary>
    /// Guards the route cache against the worst failure mode in the app: a new setting that changes
    /// the drawing but not the fingerprint. When that happens, Export returns the PREVIOUS
    /// <c>.tdld</c> from the cache and logs "Reusing cached route (image and settings unchanged)",
    /// so the old drawing is flashed to hardware with a log line asserting it is correct.
    /// <para>
    /// This is driven by reflection rather than a hand-written list precisely so that adding a
    /// property to <see cref="DrawImageSettings"/> and forgetting
    /// <see cref="RouteFingerprint"/> fails here instead of in someone's Palette House.
    /// </para>
    /// </summary>
    public class FingerprintTests
    {
        private static DrawImageSettings Baseline() =>
            new()
            {
                QuantizerSettings = new QuantizerSettings("Arbitrary", 32, false),
                DenoiserName = null,
                TSPTimeLimit = 1.0f,
                DisableLargeBrush = false,
                EnableExperimentalFeatures = false,
                HomeToTopLeft = false,
                ReverseColourOrder = false,
                EarlyTspExitEnabled = false,
                EarlyTspExitRateCoefficient = 0.05,
                EarlyTspExitSolutionsDistance = 10,
            };

        private static string Describe(DrawImageSettings s) =>
            (string)
                typeof(RouteFingerprint)
                    .GetMethod("Describe", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [64, 64, s, SwitchVersion.Switch2])!;

        /// <summary>Flips or perturbs a property to a value that differs from the baseline.</summary>
        private static bool TryMutate(PropertyInfo prop, DrawImageSettings target)
        {
            var t = prop.PropertyType;
            if (t == typeof(bool))
            {
                prop.SetValue(target, !(bool)prop.GetValue(target)!);
                return true;
            }
            if (t == typeof(float))
            {
                prop.SetValue(target, (float)prop.GetValue(target)! + 1.5f);
                return true;
            }
            if (t == typeof(double))
            {
                prop.SetValue(target, (double)prop.GetValue(target)! + 0.25);
                return true;
            }
            if (t == typeof(int))
            {
                prop.SetValue(target, (int)prop.GetValue(target)! + 7);
                return true;
            }
            if (t == typeof(string))
            {
                prop.SetValue(target, "Median");
                return true;
            }
            if (t == typeof(QuantizerSettings))
            {
                prop.SetValue(target, new QuantizerSettings("CieLab", 64, true));
                return true;
            }
            return false;
        }

        [Fact]
        public void Every_DrawImageSettings_property_changes_the_fingerprint()
        {
            var props = typeof(DrawImageSettings)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .ToList();

            Assert.NotEmpty(props);

            var baseline = Describe(Baseline());
            var unhandled = new List<string>();
            var ignored = new List<string>();

            foreach (var prop in props)
            {
                var mutated = Baseline();
                if (!TryMutate(prop, mutated))
                {
                    // A type this test does not know how to perturb. Fail loudly rather than
                    // quietly skipping it, since a skipped property is exactly the bug.
                    unhandled.Add($"{prop.Name} ({prop.PropertyType.Name})");
                    continue;
                }

                if (Describe(mutated) == baseline)
                    ignored.Add(prop.Name);
            }

            Assert.True(
                unhandled.Count == 0,
                "FingerprintTests cannot perturb these property types — extend TryMutate: "
                    + string.Join(", ", unhandled)
            );
            Assert.True(
                ignored.Count == 0,
                "These DrawImageSettings properties do NOT affect RouteFingerprint, so changing "
                    + "them would silently reuse a stale cached route: "
                    + string.Join(", ", ignored)
            );
        }

        [Fact]
        public void Identical_settings_produce_identical_fingerprints()
        {
            Assert.Equal(Describe(Baseline()), Describe(Baseline()));
        }

        [Fact]
        public void Image_size_and_switch_version_are_part_of_the_fingerprint()
        {
            var describe = typeof(RouteFingerprint).GetMethod(
                "Describe",
                BindingFlags.NonPublic | BindingFlags.Static
            )!;
            var s = Baseline();
            var a = (string)describe.Invoke(null, [64, 64, s, SwitchVersion.Switch2])!;
            var differentSize = (string)describe.Invoke(null, [64, 65, s, SwitchVersion.Switch2])!;
            var differentVer = (string)describe.Invoke(null, [64, 64, s, SwitchVersion.Switch1])!;

            Assert.NotEqual(a, differentSize);
            Assert.NotEqual(a, differentVer);
        }
    }
}
