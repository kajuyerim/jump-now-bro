using NUnit.Framework;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests
{
    public class RttEstimatorTests
    {
        [Test]
        public void BeforeAnySample_ReturnsInitial()
        {
            var rtt = new RttEstimator(initialRttSeconds: 0.1f);
            Assert.AreEqual(0.1f, rtt.RttSeconds, 1e-6f);
        }

        [Test]
        public void FirstSample_SeedsDirectly_NoBlendFromInitial()
        {
            var rtt = new RttEstimator(initialRttSeconds: 0.1f, smoothing: 0.125f);
            rtt.AddSample(0.05f);
            Assert.AreEqual(0.05f, rtt.RttSeconds, 1e-6f);
        }

        [Test]
        public void SubsequentSamples_BlendViaEma()
        {
            var rtt = new RttEstimator(smoothing: 0.5f);
            rtt.AddSample(0.10f);                       // seed -> 0.10
            rtt.AddSample(0.20f);                       // 0.10 + 0.5*(0.20-0.10) = 0.15
            Assert.AreEqual(0.15f, rtt.RttSeconds, 1e-6f);
        }

        [Test]
        public void RepeatedSamples_ConvergeTowardLatest()
        {
            var rtt = new RttEstimator(smoothing: 0.2f);
            rtt.AddSample(0.10f);                       // seed
            for (int i = 0; i < 100; i++) rtt.AddSample(0.03f);
            Assert.AreEqual(0.03f, rtt.RttSeconds, 1e-3f);
        }

        [Test]
        public void NegativeSample_Ignored()
        {
            var rtt = new RttEstimator();
            rtt.AddSample(0.05f);
            rtt.AddSample(-1f);
            Assert.AreEqual(0.05f, rtt.RttSeconds, 1e-6f);
        }

        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.NegativeInfinity)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(RttEstimator.MaxSampleSeconds + 0.001f)]
        public void InvalidFirstSample_DoesNotSeedOrChangeDiagnostics(float sample)
        {
            var rtt = new RttEstimator();
            rtt.AddSample(sample);
            Assert.AreEqual(0.1f, rtt.RttSeconds);
            Assert.AreEqual(0f, rtt.LastSampleSeconds);
            rtt.AddSample(0.04f);
            Assert.AreEqual(0.04f, rtt.RttSeconds, 1e-6f);
        }

        [TestCase(-1f)]
        [TestCase(float.NaN)]
        [TestCase(float.NegativeInfinity)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(RttEstimator.MaxSampleSeconds + 0.001f)]
        public void InvalidSample_DoesNotBlendOrChangeDiagnostics(float sample)
        {
            var rtt = new RttEstimator();
            rtt.AddSample(0.2f);
            rtt.AddSample(sample);
            Assert.AreEqual(0.2f, rtt.RttSeconds);
            Assert.AreEqual(0.2f, rtt.LastSampleSeconds);
            rtt.AddSample(0.4f);
            Assert.AreEqual(0.225f, rtt.RttSeconds, 1e-6f);
        }

        [TestCase(0f)]
        [TestCase(RttEstimator.MaxSampleSeconds)]
        public void SampleAtBoundary_IsAccepted(float sample)
        {
            var rtt = new RttEstimator();
            rtt.AddSample(sample);
            Assert.AreEqual(sample, rtt.RttSeconds);
            Assert.AreEqual(sample, rtt.LastSampleSeconds);
        }
    }
}
