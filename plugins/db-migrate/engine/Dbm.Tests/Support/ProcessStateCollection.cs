namespace Dbm.Tests.Support;

/// <summary>Tests that change process-wide state (environment variables, current directory) run alone in this collection.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessStateCollection
{
    public const string Name = "process-state";
}
