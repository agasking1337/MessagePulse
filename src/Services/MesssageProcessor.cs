namespace MsgPulse.Services;

using SwDevtools.Players;
using SwDevtools.Logging;
using SwiftlyS2.Shared;
using PlaceholderAPI.Contract;
using SwiftlyS2.Shared.Players;
using System.Text.RegularExpressions;

/// <summary>Processes messages and sends them to players.</summary>
public sealed class MessageProcessor(ISwiftlyCore core, IPluginLogger logger, ITranslate translations)
{
    public IPlaceholderAPIv1? PlaceholderApi { get; set; }
    private static readonly Regex msgPulsePrefix = new(@"\{MP_PREFIX\}", RegexOptions.Compiled);

    private static readonly Regex lineBreak = new(@"<br\s*/?>|\r?\n", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Attaches the PlaceholderAPI to the message processor. And registers the 'mp_prefix' placeholder.</summary>
    public void AttachPlaceholderApi(IPlaceholderAPIv1 api)
    {
        if (this.PlaceholderApi != null) return;

        this.PlaceholderApi = api;

        try
        {
            api.RegisterPlaceholder(
                "mp_prefix", msgPulsePrefix, (player, context) => core.Localizer["chat.prefix"]
            );
        }
        catch (Exception ex)
        {
            logger.Error("[MessageProcessor] Failed to register mp_prefix placeholder:[/] {0}", ex.Message);
        }
    }

    /// <summary>Processes a message for a specific player, replacing placeholders and translating if necessary.</summary>
    public string ProcessMessage(IPlayer? reciever, string message) =>
        ReplacePlaceholders(IsTranslationKey(message) ? GetTranslatedMessage(message, reciever) : message, reciever);

    /// <summary>Sends a message to all valid players.</summary>
    public void SendToAll(string message)
    {
        foreach (var reciever in core.PlayerManager.GetAllValidPlayers())
        {
            SendToPlayer(reciever, message);
        }
    }

    /// <summary>Sends a message to a specific player.</summary>
    public void SendToPlayer(IPlayer reciever, string message)
    {
        var processed = ProcessMessage(reciever, message);

        foreach (var line in SplitLines(processed))
            reciever.SendMessage(MessageType.Chat, line);
    }

    /// <summary>Splits a message into non-empty lines on line break tags or newlines.</summary>
    public IEnumerable<string> SplitLines(string message) =>
        lineBreak.Split(message).Where(l => !string.IsNullOrWhiteSpace(l));

    /// <summary>Gets the translated message for a specific player.</summary>
    public string GetTranslatedMessage(string message, IPlayer? player) =>
        translations.Localize(player, message);

    /// <summary>Replaces placeholders in a message for a specific player.</summary>
    public string ReplacePlaceholders(string message, IPlayer? player) =>
        this.PlaceholderApi?.ProcessMessage(player, message) ?? message;

    /// <summary>Checks if a message is a translation key.</summary>
    private bool IsTranslationKey(string message) =>
        message.StartsWith("chat.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Releases the PlaceholderAPI attachment.</summary>
    public void Release()
    {
        if (PlaceholderApi == null) return;

        try
        {
            PlaceholderApi.UnregisterPlaceholder("mp_prefix");
        }
        catch (Exception ex)
        {
            logger.Error("[MessageProcessor] Failed to unregister placeholder 'mp_prefix':[/] {0}", ex.Message);
        }
        finally
        {
            PlaceholderApi = null;
        }
    }
}
