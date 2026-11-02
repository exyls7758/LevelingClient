using Multiverse.Services;

int failures = 0;
var cases = HostVideoPipelineControllerIntegrationTests.Cases().ToArray();
foreach ((string name, Func<Task> test) in cases)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {name}");
        Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine($"Host controller integration: {cases.Length - failures}/{cases.Length} passed");
return failures == 0 ? 0 : 1;
