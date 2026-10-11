namespace MsgPulse.Services;

using Config;
using Models;
using SwDevtools.Configuration;
using SwDevtools.Logging;
using SwiftlyS2.Shared;

/// <summary>Handles creation of custom commands.</summary>
public sealed class CustomCommandsHandler(
    ISwiftlyCore core,
    IPluginLogger logger,
    MessageProcessor processor,
    IJsonConfig jsonConfig)
{
    private CommandsConfig Config { get; set; } = new();
    private readonly HashSet<string> registered = new(StringComparer.OrdinalIgnoreCase);

    public void Initialize(bool hotReload)
    {
        this.Config = jsonConfig.GetOrCreate<CommandsConfig>("custom_commands.json", "customcommands",
            core.Configuration.GetConfigPath("custom_commands.json"),
            JsonConfigInitMode.ExampleFromResources, "example_custom_commands.json"
        );

        if (hotReload)
            UnregisterAllCommands();

        foreach (var command in Config.Commands)
        {
            Register(command);
        }

        logger.Info($"[CustomCommands] Registered [green]{registered.Count}[/] custom commands!");
        logger.Info($"[CustomCommands] Commands: {string.Join(", ", registered)}");
    }

    // Command registration ¯\_(ツ)_/¯
    public void Register(CustomCommand command)
    {
        var triggers = command.Triggers;

        if (triggers.Any(registered.Contains))
        {
            logger.Error(
                $"[CustomCommands] Custom command with triggers {string.Join(", ", triggers)} is already registered."
            );
            return;
        }

        var target = command.Target.Trim();
        var replyToCaller =
            target.Equals("caller", StringComparison.OrdinalIgnoreCase)
            || target.Equals("player", StringComparison.OrdinalIgnoreCase);

        foreach (var trigger in triggers)
        {
            core.Command.RegisterCommand(trigger, ctx =>
                {
                    if (replyToCaller)
                    {
                        foreach (var line in processor.SplitLines(processor.ProcessMessage(ctx.Sender, command.Message)))
                            ctx.Reply(line);
                    }
                    else
                    {
                        processor.SendToAll(command.Message);
                    }
                }, registerRaw: true
            );

            registered.Add(trigger);
        }
    }

    public void UnregisterAllCommands()
    {
        foreach (var command in registered)
        {
            core.Command.UnregisterCommand(command);
        }

        registered.Clear();
    }

    public void Release()
    {
        UnregisterAllCommands();
    }
}
