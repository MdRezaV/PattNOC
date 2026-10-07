namespace ServiceLib.Tests.Handler;

public class TestExecutionOptionsTests
{
    [Test]
    public async Task Normalize_SeedsDefaults_MigratingFromLegacyFields()
    {
        var item = new SpeedTestItem
        {
            SpeedTestTimeout = 10,
            MixedConcurrencyCount = 10,
        };

        ConfigHandler.NormalizeTestExecutionOptions(item);

        await item.RealDelayTimeoutSeconds.Should().BeEqualTo(Global.RealDelayTimeoutSecondsDefault);
        await item.RealDelayConcurrentCount.Should().BeEqualTo(10);
        await item.RealDelayRetryCount.Should().BeEqualTo(Global.TestRetryCountDefault);
        await item.MultiDelayTimeoutSeconds.Should().BeEqualTo(Global.RealDelayTimeoutSecondsDefault);
        await item.MultiSpeedTimeoutSeconds.Should().BeEqualTo(10);
        await item.MultiConcurrentCount.Should().BeEqualTo(10);
        await item.MultiRetryCount.Should().BeEqualTo(Global.TestRetryCountDefault);
    }

    [Test]
    public async Task Normalize_DefaultRetryCount_IsOne()
    {
        var item = new SpeedTestItem
        {
            SpeedTestTimeout = 10,
            MixedConcurrencyCount = 10,
        };

        // Property initializers seed 1 for configs serialized before retry existed.
        await item.RealDelayRetryCount.Should().BeEqualTo(1);
        await item.MultiRetryCount.Should().BeEqualTo(1);

        ConfigHandler.NormalizeTestExecutionOptions(item);

        await item.RealDelayRetryCount.Should().BeEqualTo(1);
        await item.MultiRetryCount.Should().BeEqualTo(1);
        await Global.TestRetryCountDefault.Should().BeEqualTo(1);
    }

    [Test]
    public async Task Normalize_ClampsOutOfRangeValues()
    {
        var item = new SpeedTestItem
        {
            SpeedTestTimeout = 10,
            MixedConcurrencyCount = 10,
            RealDelayTimeoutSeconds = 1000,
            RealDelayConcurrentCount = 100,
            RealDelayRetryCount = 99,
            MultiDelayTimeoutSeconds = 1000,
            MultiSpeedTimeoutSeconds = 1000,
            MultiConcurrentCount = 100,
            MultiRetryCount = 99,
        };

        ConfigHandler.NormalizeTestExecutionOptions(item);

        await item.RealDelayTimeoutSeconds.Should().BeEqualTo(Global.TestTimeoutSecondsMax);
        await item.RealDelayConcurrentCount.Should().BeEqualTo(Global.SpeedTestConcurrencyCountMax);
        await item.RealDelayRetryCount.Should().BeEqualTo(Global.TestRetryCountMax);
        await item.MultiDelayTimeoutSeconds.Should().BeEqualTo(Global.TestTimeoutSecondsMax);
        await item.MultiSpeedTimeoutSeconds.Should().BeEqualTo(Global.TestTimeoutSecondsMax);
        await item.MultiConcurrentCount.Should().BeEqualTo(Global.SpeedTestConcurrencyCountMax);
        await item.MultiRetryCount.Should().BeEqualTo(Global.TestRetryCountMax);
        await Global.SpeedTestConcurrencyCountMax.Should().BeEqualTo(30);
    }

    [Test]
    public async Task Normalize_PreservesExplicitZeroRetry()
    {
        var item = new SpeedTestItem
        {
            SpeedTestTimeout = 10,
            MixedConcurrencyCount = 10,
            RealDelayRetryCount = 0,
            MultiRetryCount = 0,
        };

        ConfigHandler.NormalizeTestExecutionOptions(item);

        // Zero is a valid choice (no retry) and must survive normalization.
        await item.RealDelayRetryCount.Should().BeEqualTo(0);
        await item.MultiRetryCount.Should().BeEqualTo(0);
    }

    [Test]
    public async Task Normalize_KeepsUserConfiguredValues()
    {
        var item = new SpeedTestItem
        {
            SpeedTestTimeout = 10,
            MixedConcurrencyCount = 10,
            RealDelayTimeoutSeconds = 7,
            RealDelayConcurrentCount = 5,
            RealDelayRetryCount = 2,
            MultiDelayTimeoutSeconds = 8,
            MultiSpeedTimeoutSeconds = 30,
            MultiConcurrentCount = 12,
            MultiRetryCount = 3,
        };

        ConfigHandler.NormalizeTestExecutionOptions(item);

        await item.RealDelayTimeoutSeconds.Should().BeEqualTo(7);
        await item.RealDelayConcurrentCount.Should().BeEqualTo(5);
        await item.RealDelayRetryCount.Should().BeEqualTo(2);
        await item.MultiDelayTimeoutSeconds.Should().BeEqualTo(8);
        await item.MultiSpeedTimeoutSeconds.Should().BeEqualTo(30);
        await item.MultiConcurrentCount.Should().BeEqualTo(12);
        await item.MultiRetryCount.Should().BeEqualTo(3);
    }
}
