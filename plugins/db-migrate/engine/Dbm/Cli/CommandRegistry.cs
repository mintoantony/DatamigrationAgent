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
        new DiscoverCommand(),   // T2.5
        new SearchCommand(),     // T2.5
        new ShowCommand(),       // T2.5
        new MapAutoCommand(),    // T3.3
        new SqlGenCommand(),     // T4.3
        new SqlValidateCommand(), // T4.3
        new TransferStartCommand(),   // T5.5
        new TransferPauseCommand(),   // T5.5
        new TransferResumeCommand(),  // T5.5
        new TransferCancelCommand(),  // T5.5
        new TransferStatusCommand(),  // T5.5
        // Later milestones append their commands below this line.
    ];
}
