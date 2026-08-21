using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Rebaka.Editor.Preflight;
using UnityEngine;
using UnityEngine.TestTools;

namespace Rebaka.Editor.Tests
{
    public sealed class PreflightProfileRunnerTests
    {
        [Test]
        public void Run_PreconditionFails_DoesNotRunNormalChecks()
        {
            var normalCheck = new FakeCheck("Normal", PreflightResult.Pass("normal"));

            PreflightRunResult result = new PreflightProfileRunner().Run(CreateDefinition(
                PreflightProfile.ProductionIntegration,
                blocksDevelopIntegrationWhenFail: true,
                new FakeCheck("Precondition", PreflightResult.Fail("precondition")),
                normalCheck));

            Assert.That(result.Results.Count, Is.EqualTo(1));
            Assert.That(result.Results[0].Result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(normalCheck.RunCount, Is.EqualTo(0));
        }

        [Test]
        public void Run_PreconditionWarning_DoesNotRunNormalChecks()
        {
            var normalCheck = new FakeCheck("Normal", PreflightResult.Pass("normal"));

            PreflightRunResult result = new PreflightProfileRunner().Run(CreateDefinition(
                PreflightProfile.ProductionIntegration,
                blocksDevelopIntegrationWhenFail: true,
                new FakeCheck("Precondition", PreflightResult.Warn("precondition")),
                normalCheck));

            Assert.That(result.Results.Count, Is.EqualTo(1));
            Assert.That(result.Results[0].Result.Status, Is.EqualTo(PreflightStatus.Warning));
            Assert.That(normalCheck.RunCount, Is.EqualTo(0));
        }

        [Test]
        public void Run_PreconditionThrows_RecordsFailAndDoesNotRunNormalChecks()
        {
            var normalCheck = new FakeCheck("Normal", PreflightResult.Pass("normal"));
            LogAssert.Expect(LogType.Exception, new Regex("precondition failure"));

            PreflightRunResult result = new PreflightProfileRunner().Run(CreateDefinition(
                PreflightProfile.ProductionIntegration,
                blocksDevelopIntegrationWhenFail: true,
                new ThrowingCheck("Precondition", "precondition failure"),
                normalCheck));

            Assert.That(result.Results.Count, Is.EqualTo(1));
            Assert.That(result.Results[0].Result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(normalCheck.RunCount, Is.EqualTo(0));
        }

        [Test]
        public void Run_OneCheckThrows_RecordsFailAndContinues()
        {
            var laterCheck = new FakeCheck("Later", PreflightResult.Pass("later"));
            LogAssert.Expect(LogType.Exception, new Regex("normal failure"));

            PreflightRunResult result = new PreflightProfileRunner().Run(CreateDefinition(
                PreflightProfile.ProductionIntegration,
                blocksDevelopIntegrationWhenFail: true,
                new FakeCheck("Precondition", PreflightResult.Pass("precondition")),
                new ThrowingCheck("Throwing", "normal failure"),
                laterCheck));

            Assert.That(result.Results.Count, Is.EqualTo(3));
            Assert.That(result.Results[1].Result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(laterCheck.RunCount, Is.EqualTo(1));
        }

        [Test]
        public void Run_TwoDefinitions_ReturnsIndependentResultSnapshots()
        {
            var runner = new PreflightProfileRunner();
            PreflightRunResult production = runner.Run(CreateDefinition(
                PreflightProfile.ProductionIntegration,
                blocksDevelopIntegrationWhenFail: true,
                new FakeCheck("Production precondition", PreflightResult.Pass("pass")),
                new FakeCheck("Production check", PreflightResult.Pass("pass"))));
            PreflightRunResult map = runner.Run(CreateDefinition(
                PreflightProfile.MapPrototype,
                blocksDevelopIntegrationWhenFail: false,
                new FakeCheck("Map precondition", PreflightResult.Pass("pass")),
                new FakeCheck("Map check", PreflightResult.Warn("warn"))));

            Assert.That(production.Results.Count, Is.EqualTo(2));
            Assert.That(production.Results[1].Name, Is.EqualTo("Production check"));
            Assert.That(map.Results.Count, Is.EqualTo(2));
            Assert.That(map.Results[1].Name, Is.EqualTo("Map check"));
        }

        [Test]
        public void BlocksDevelopIntegration_ProductionFailOnly_ReturnsTrue()
        {
            PreflightRunResult result = new PreflightProfileRunner().Run(CreateDefinition(
                PreflightProfile.ProductionIntegration,
                blocksDevelopIntegrationWhenFail: true,
                new FakeCheck("Precondition", PreflightResult.Fail("fail"))));

            Assert.That(result.BlocksDevelopIntegration, Is.True);
        }

        [Test]
        public void BlocksDevelopIntegration_MapPrototypeFail_ReturnsFalse()
        {
            PreflightRunResult result = new PreflightProfileRunner().Run(CreateDefinition(
                PreflightProfile.MapPrototype,
                blocksDevelopIntegrationWhenFail: false,
                new FakeCheck("Precondition", PreflightResult.Fail("fail"))));

            Assert.That(result.BlocksDevelopIntegration, Is.False);
        }

        private static PreflightProfileDefinition CreateDefinition(
            PreflightProfile profile,
            bool blocksDevelopIntegrationWhenFail,
            IPreflightCheck precondition,
            params IPreflightCheck[] checks)
        {
            return new PreflightProfileDefinition(
                profile,
                "Test profile",
                "Assets/Level/Scenes/Test_Playground.unity",
                blocksDevelopIntegrationWhenFail,
                precondition,
                checks);
        }

        private sealed class FakeCheck : IPreflightCheck
        {
            private readonly PreflightResult _result;

            public FakeCheck(string name, PreflightResult result)
            {
                Name = name;
                _result = result;
            }

            public string Name { get; }
            public int RunCount { get; private set; }

            public PreflightResult Run()
            {
                RunCount++;
                return _result;
            }
        }

        private sealed class ThrowingCheck : IPreflightCheck
        {
            private readonly string _message;

            public ThrowingCheck(string name, string message)
            {
                Name = name;
                _message = message;
            }

            public string Name { get; }

            public PreflightResult Run()
            {
                throw new InvalidOperationException(_message);
            }
        }
    }
}
