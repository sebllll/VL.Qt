using NUnit.Framework;

/// <summary>
/// Scenarios that must run last: they leave windows open or workers busy at the end of the run. NUnit runs fixtures in the
/// alphabetical order of their names (observed, not guaranteed), so this one is named to sort after all others.
/// </summary>
[TestFixture, NonParallelizable]
public static class ZzLastTest
{
    /// <summary>Clear pin on window and offscreen scene; leaves two VSync windows open on purpose (end of the process with open windows on the shared device).</summary>
    [Test, Category("clear-pin"), Category("ClearPin")]
    public static void ClearPin() => Scenario.Run(ClearPinTest.Run);
}
