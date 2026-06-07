using DiplomaMethod.Core.Utils;

namespace DiplomaMethod.UnitTests.UtilsTests
{
    [TestFixture]
    public class UtilsTests
    {
        [Test]
        [TestCase(new double[] { 10, 10, 10, 30, 10, 10 }, new int[] { 3 })]
        [TestCase(new double[] { 50, 50, 50, 50, 50, 10 }, new int[] { 5 })]
        [TestCase(new double[] { 10, 10, 10, 10 }, new int[] { })]
        [TestCase(new double[] { 10, 12, 10, 11, 40, 10 }, new int[] { 4 })]
        public void MathUtils_GetPeaks_ShouldReturnCorrectOutliers(double[] lineBorders, int[] expectedPeaks)
        {
            var result = MathUtils.GetPeaks(lineBorders);
            Assert.That(result, Is.EquivalentTo(expectedPeaks));
        }
    }
}
