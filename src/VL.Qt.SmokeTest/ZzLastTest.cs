using NUnit.Framework;

/// <summary>
/// Tests that must run after all others (they leave state behind on purpose). NUnit runs fixtures in the alphabetical order
/// of their names (observed, not guaranteed), hence the name: this fixture is last by convention.
/// </summary>
[TestFixture, NonParallelizable]
public static class ZzLastTest
{
    /// <summary>
    /// Process end with busy workers (#359): three 10 s decodes are left running on purpose. Not at the end of the
    /// resources test: with a single worker (2-core CI runners) they would queue 30 s in front of every later decode
    /// (image-resource).
    /// </summary>
    [Test, Category("resources-leave-jobs"), Category("resources")]
    public static void ResourcesLeaveJobs() => Scenario.Run(s => ResourceTest.LeaveRunningJobs(s));
}
