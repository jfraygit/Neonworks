using Nightshare.Core;
using Xunit;

namespace Nightshare.Tests
{
    public class AngleMathTests
    {
        [Theory]
        [InlineData(0f, 10f, 10f)]
        [InlineData(10f, 0f, -10f)]
        [InlineData(359f, 1f, 2f)]      // through north, forwards
        [InlineData(1f, 359f, -2f)]     // through north, backwards
        [InlineData(0f, 180f, 180f)]
        [InlineData(90f, 270f, 180f)]
        public void ShortestDeltaTakesTheShortWayRound(float from, float to, float expected)
        {
            Assert.Equal(expected, AngleMath.ShortestDelta(from, to), 3);
        }

        [Fact]
        public void ShortestDeltaIsZeroForTheSameAngle()
        {
            Assert.Equal(0f, AngleMath.ShortestDelta(137f, 137f), 3);
        }
    }

    public class YawAccumulatorTests
    {
        /// <summary>
        /// The exact bug this type exists to prevent. Max minus min reports a full circle
        /// as zero, so a rig probe built that way scores a complete turn as no rotation.
        /// </summary>
        [Fact]
        public void AFullCircleReadsAsAFullCircle()
        {
            var acc = new YawAccumulator();
            for (int deg = 0; deg <= 360; deg += 5) acc.Add(deg % 360f);

            Assert.InRange(acc.Travel, 355f, 365f);
        }

        [Fact]
        public void TwoFullCirclesReadAsSevenHundredAndTwenty()
        {
            var acc = new YawAccumulator();
            for (int deg = 0; deg <= 720; deg += 5) acc.Add(deg % 360f);

            Assert.InRange(acc.Travel, 715f, 725f);
        }

        [Fact]
        public void AQuarterTurnReadsAsNinety()
        {
            var acc = new YawAccumulator();
            for (int deg = 0; deg <= 90; deg += 3) acc.Add(deg);

            Assert.InRange(acc.Travel, 88f, 92f);
        }

        /// <summary>Turning through north must not be read as a near full rotation.</summary>
        [Fact]
        public void TurningThroughNorthIsMeasuredTheShortWay()
        {
            var acc = new YawAccumulator();
            acc.Add(350f);
            acc.Add(355f);
            acc.Add(0f);
            acc.Add(5f);
            acc.Add(10f);

            Assert.InRange(acc.Travel, 18f, 22f);
        }

        /// <summary>Turning out and back covers the ground twice, not zero.</summary>
        [Fact]
        public void TurningThereAndBackCountsBothWays()
        {
            var acc = new YawAccumulator();
            for (int deg = 0; deg <= 90; deg += 5) acc.Add(deg);
            for (int deg = 85; deg >= 0; deg -= 5) acc.Add(deg);

            Assert.InRange(acc.Travel, 175f, 185f);
        }

        /// <summary>Idle animation wobble must stay far below a deliberate turn.</summary>
        [Fact]
        public void SmallWobbleStaysSmall()
        {
            var acc = new YawAccumulator();
            for (int i = 0; i < 50; i++) acc.Add(48f + (i % 2 == 0 ? 0.2f : -0.2f));

            Assert.True(acc.Travel < 25f, $"wobble accumulated to {acc.Travel}");
            Assert.True(acc.PeakStep < 1f, $"peak step was {acc.PeakStep}");
        }

        /// <summary>Peak step is what separates a real turn from accumulated jitter.</summary>
        [Fact]
        public void PeakStepIdentifiesADeliberateTurn()
        {
            var acc = new YawAccumulator();
            acc.Add(0f);
            acc.Add(1f);
            acc.Add(45f);    // a real turn between samples
            acc.Add(46f);

            Assert.InRange(acc.PeakStep, 43f, 45f);
        }

        [Fact]
        public void ASingleSampleHasNoTravel()
        {
            var acc = new YawAccumulator();
            acc.Add(123f);

            Assert.Equal(0f, acc.Travel);
            Assert.Equal(1, acc.Samples);
        }

        [Fact]
        public void ResetClearsEverything()
        {
            var acc = new YawAccumulator();
            acc.Add(0f);
            acc.Add(90f);
            acc.Reset();

            Assert.Equal(0f, acc.Travel);
            Assert.Equal(0, acc.Samples);
        }
    }
}
