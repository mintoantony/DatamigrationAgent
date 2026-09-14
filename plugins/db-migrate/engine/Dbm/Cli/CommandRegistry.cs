using Dbm.Cli.Commands;

namespace Dbm.Cli;

public static class CommandRegistry
{
    public static IReadOnlyList<ICommand> All() =>
    [
        // M0
        new HelpCommand(),
        new VersionCommand(),
        new DoctorCommand(),
        // M1
        new InitCommand(),
        new StatusCommand(),
        new PauseCommand(),
        new ResumeCommand(),
        new ServeCommand(),
        new UiCommand(),
        new StopCommand(),
        new NextCommand(),
        new AwaitCommand(),
        new ApplyCommand(),
        new ArtifactCommand(),
        new FeedbackCommand(),
        new RunJobsCommand(),
        // Later milestones append their commands below this line.
    ];
}
