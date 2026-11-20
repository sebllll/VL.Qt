using NUnit.Framework;

/// <summary>
/// Tests that must run after all others: they leave windows open or workers busy on purpose. NUnit runs fixtures in the
/// alphabetical order of their names (observed, not guaranteed), hence the name: this fixture is last by convention.
/// Inside the fixture, [Order] keeps the order of the former runner: resources-leave-jobs, then clear-pin.
/// </summary>
[TestFixture, NonParallelizable]
public static class ZzLastTest
{
    /// <summary>
    /// Process end with busy workers (#359): three 10 s decodes are left running on purpose. Not at the end of the
    /// resources test: with a single worker (2-core CI runners) they would queue 30 s in front of every later decode
    /// (image-resource).
    /// </summary>
    [Test, Order(1), Category("resources-leave-jobs"), Category("resources")]
    public static void ResourcesLeaveJobs() => Scenario.Run(s => ResourceTest.LeaveRunningJobs(s));

    /// <summary>Clear pin on window and offscreen scene; leaves two VSync windows open on purpose (end of the process with open windows on the shared device).</summary>
    [Test, Order(2), Category("clear-pin"), Category("ClearPin")]
    public static void ClearPin() => Scenario.Run(ClearPinTest.Run);
}
