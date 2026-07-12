using MyFolder.Editor.Preflight;
using NUnit.Framework;

namespace MyFolder.Editor.Tests
{
    public sealed class WeaveAssembliesCheckTests
    {
        [Test]
        public void Evaluate_PassesWhenRequiredAssemblyListed()
        {
            const string json = "{\"AssembliesToWeave\":[\"Assembly-CSharp\",\"MyProject.Scripts\"]}";
            var result = WeaveAssembliesCheck.Evaluate(json, "MyProject.Scripts");
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Pass));
        }

        [Test]
        public void Evaluate_FailsWhenRequiredAssemblyMissing()
        {
            const string json = "{\"AssembliesToWeave\":[\"Assembly-CSharp\"]}";
            var result = WeaveAssembliesCheck.Evaluate(json, "MyProject.Scripts");
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
        }

        [Test]
        public void Evaluate_WarnsWhenJsonHasNoWeaveArray()
        {
            var result = WeaveAssembliesCheck.Evaluate("{}", "MyProject.Scripts");
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Warning));
        }

        [Test]
        public void Evaluate_WarnsWhenJsonIsBroken()
        {
            var result = WeaveAssembliesCheck.Evaluate("not-json", "MyProject.Scripts");
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Warning));
        }
    }
}
