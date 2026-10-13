namespace MsgPulse.Services;

using SwDevtools.Configuration;
using System.Reflection;
using System.Text.RegularExpressions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using Config;
using SwDevtools.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Natives;
using System.Collections.Concurrent;

/// <summary>Handles dynamic events and sends messages to players based on configured rules.</summary>
public sealed class DynamicEvents
{
    /// <summary>Represents a compiled rule for event messages.</summary>
    private sealed class CompiledRule
    {
        public required Func<object, string> formatter;
        public string? target;
    }

    // Properties
    private ISwiftlyCore Core { get; }
    private IPluginLogger Logger { get; }
    private MessageProcessor Processor { get; }
    private EventMessagesConfig Config { get; set; }

    private readonly HashSet<Type> registeredEvents = new();
    private readonly ConcurrentDictionary<Type, List<CompiledRule>> rulesByEventType = new();

    private static readonly Dictionary<string, string> accessorRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Player"] = "userid",
        ["PlayerController"] = "userid",
        ["UserIdPlayer"] = "userid",
        ["UserId"] = "userid",
        ["Attacker"] = "attacker",
        ["Victim"] = "userid"
    };

    private static readonly Regex tokenRegex = new(@"\{([^{}]+)\}", RegexOptions.Compiled);


    public DynamicEvents(ISwiftlyCore core, IPluginLogger logger, MessageProcessor processor,
        IJsonConfig jsonConfig)
    {
        this.Core = core;
        this.Logger = logger;
        this.Processor = processor;

        this.Config = jsonConfig.GetOrCreate<EventMessagesConfig>(
            "event_messages.json", "eventmessages",
            core.Configuration.GetConfigPath("event_messages.json"),
            JsonConfigInitMode.ExampleFromResources, "example_event_messages.json"
        );

        core.Registrator.Register(this);
    }

    public void Initialize(bool hotReload)
    {
        if (!Config.Enabled) return;

        if (hotReload)
        {
            this.rulesByEventType.Clear();
        }

        foreach (var rule in Config.Rules)
        {
            if (string.IsNullOrEmpty(rule.Event)) continue;

            var eventType = ResolveEventType(rule.Event);
            if (eventType == null)
            {
                Logger.Error($"[EventMessages] Failed to resolve event type for rule {rule.Event}");
                continue;
            }

            var list = rulesByEventType.GetOrAdd(eventType, _ => new List<CompiledRule>());
            list.Add(new CompiledRule { formatter = CompileTemplate(rule.Message, eventType), target = rule.Target });

            RegisterEvent(eventType);
        }
    }

    // ------------------------------
    // Event Registration
    // ------------------------------

    /// <summary>Registers an event type to receive game events.</summary>
    private void RegisterEvent(Type eventType)
    {
        if (registeredEvents.Contains(eventType))
            return;

        var gameEventService = Core.GameEvent;

        var hookPost = gameEventService.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m is { Name: "HookPost", IsGenericMethodDefinition: true } &&
                m.GetParameters().Length == 1
            );

        if (hookPost == null)
        {
            Logger.Warn("[EventMessages] HookPost not found");
            return;
        }

        var genericHook = hookPost.MakeGenericMethod(eventType);

        var handler = GetType()
            .GetMethod(nameof(OnGameEventGeneric), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(eventType);

        var del = Delegate.CreateDelegate(
            genericHook.GetParameters()[0].ParameterType,
            this,
            handler
        );

        genericHook.Invoke(gameEventService, [del]);
        registeredEvents.Add(eventType);

        Logger.Info($"[EventMessages] Registered event [green]{eventType.Name}[/]");
    }

    private HookResult OnGameEventGeneric<TEvent>(TEvent @event)
    {
        HandleEvent(typeof(TEvent), @event!);
        return HookResult.Continue;
    }

    // ------------------------------
    // Event Handling (╯°□°)╯︵ ┻━┻
    // ------------------------------
    private void HandleEvent(Type eventType, object @event)
    {
        if (!rulesByEventType.TryGetValue(eventType, out var rules))
            return;

        foreach (var rule in rules)
        {
            // Check if message is a translation key o.O
            var isTranslation = rule.formatter(@event).StartsWith("chat.", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(rule.target, "all", StringComparison.OrdinalIgnoreCase))
            {
                if (isTranslation)
                {
                    // For translations, we must resolve per-player to get the correct language
                    // and then inject event values into the translated string. (╯°□°)╯︵ ┻━┻
                    var rawKey = rule.formatter(@event);
                    foreach (var player in Core.PlayerManager.GetAllValidPlayers())
                    {
                        var translated = Processor.GetTranslatedMessage(rawKey, player);
                        var processed = ReplaceEventTokens(translated, @event);
                        Processor.SendToPlayer(player, processed);
                    }
                }
                else
                {
                    Processor.SendToAll(rule.formatter(@event));
                }
            }
            else if (string.Equals(rule.target, "player", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetEventPlayer(@event, "Player", out var player) && player != null)
                {
                    if (isTranslation)
                    {
                        var rawKey = rule.formatter(@event);
                        var translated = Processor.GetTranslatedMessage(rawKey, player);
                        var processed = ReplaceEventTokens(translated, @event);
                        Processor.SendToPlayer(player, processed);
                    }
                    else
                    {
                        Processor.SendToPlayer(player, rule.formatter(@event));
                    }
                }
            }
        }
    }

    private string ReplaceEventTokens(string message, object @event)
    {
        return tokenRegex.Replace(message, match =>
        {
            var token = match.Groups[1].Value;
            return GetEventValue(@event, token);
        });
    }

    private bool TryGetEventPlayer(object @event, string templateRoot, out IPlayer? player)
    {
        player = null;

        var accesorProp = @event.GetType().GetProperty("Accessor", BindingFlags.Public | BindingFlags.Instance);
        if (accesorProp?.GetValue(@event) is not IGameEventAccessor accessor)
            return false;

        if (!accessorRoots.TryGetValue(templateRoot, out var key))
            return false;

        var method = accessor.GetType().GetMethod("GetPlayer");
        if (method == null) return false;


        // Get the player from the accessor using the key (player, attacker etc)
        player = method.Invoke(accessor, [key]) as IPlayer;
        if (!IsUsable(player))
            player = null;

        return player != null;
    }

    // Get valid event properties for a specific event type
    private static IEnumerable<string> GetValidProperties(Type eventType, int maxDepth = 3)
    {
        var prefix = "";
        return GetValidPropertiesRecursive(eventType, prefix, 0, maxDepth);
    }

    // ¯\_(ツ)_/¯
    private static IEnumerable<string> GetValidPropertiesRecursive(
        Type type,
        string prefix,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth)
            yield break;

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in props)
        {
            if (!prop.CanRead)
                continue;

            var fullName = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
            yield return fullName;

            // Only recurse for user-defined classes, not primitives or system types
            if (!prop.PropertyType.IsPrimitive &&
                prop.PropertyType != typeof(string) &&
                !prop.PropertyType.IsEnum)
            {
                foreach (var sub in GetValidPropertiesRecursive(prop.PropertyType, fullName, depth + 1, maxDepth))
                    yield return sub;
            }
        }
    }

    // ------------------------------
    // Compile Event Message Template                                           ¯\_(ツ)_/¯
    // ------------------------------
    private static Func<object, string> CompileTemplate(string template, Type eventType)
    {
        var parts = new List<Func<object, string>>();
        var index = 0;

        while (true)
        {
            var start = template.IndexOf('{', index);
            if (start < 0)
            {
                if (index < template.Length)
                    parts.Add(_ => template[index..]);
                break;
            }

            if (start > index)
            {
                var text = template[index..start];
                parts.Add(_ => text);
            }

            var end = template.IndexOf('}', start + 1);
            if (end < 0) break; // unmatched, treat as literal

            var token = template[(start + 1)..end];

            // compile token into getter
            var getter = CompilePropertyGetter(eventType, token);
            parts.Add(getter);

            index = end + 1;
        }

        return evt =>
        {
            var sb = new System.Text.StringBuilder();
            foreach (var part in parts)
                sb.Append(part(evt));
            return sb.ToString();
        };
    }

    private static Func<object, string> CompilePropertyGetter(Type eventType, string path)
    {
        return evt => GetEventValue(evt, path);
    }

    private static string GetEventValue(object evt, string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = evt;
        var first = parts[0];

        // --- Accessor-based roots ---
        if (evt.GetType().GetProperty("Accessor")?.GetValue(evt) is { } accessor &&
            accessorRoots.TryGetValue(first, out var key))
        {
            var methodName = first.Equals("Player", StringComparison.OrdinalIgnoreCase)
                ? "GetPlayer"
                : "GetPlayerController";
            var method = accessor.GetType().GetMethod(methodName);
            if (method == null)
                return $"{{{path}}}";

            current = method.Invoke(accessor, [key]);
            if (!IsUsable(current))
                return string.Empty;
        }
        else
        {
            // --- Direct event property ---
            var prop = current.GetType().GetProperty(first,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null)
                return $"{{{path}}}";
            current = prop.GetValue(current);
        }

        // --- Remaining nested parts ---
        for (var i = 1; i < parts.Length; i++)
        {
            if (!IsUsable(current))
                return string.Empty;

            var prop = current!.GetType().GetProperty(parts[i],
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null)
                return $"{{{path}}}";

            current = prop.GetValue(current);
        }

        return IsUsable(current) ? current!.ToString() ?? string.Empty : string.Empty;
    }

    /// <summary>
    /// Native-backed objects (schema classes, players) can wrap a null/stale pointer (e.g. the accessor returns
    /// a wrapper around address 0 for unresolved userid/attacker). Reading their fields dereferences native
    /// memory and segfaults the server, so they must be validated first.
    /// </summary>
    private static bool IsUsable(object? value) => value switch
    {
        null => false,
        INativeHandle handle => handle.IsValid,
        IPlayer player => player.IsValid,
        _ => true
    };


    // ------------------------------
    // Event Type Resolution
    // ------------------------------
    private static Type? ResolveEventType(string name)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(
                $"SwiftlyS2.Shared.GameEventDefinitions.{name}",
                false,
                true))
            .FirstOrDefault(t => t != null);
    }

    public void Release()
    {
        registeredEvents.Clear();
        rulesByEventType.Clear();
    }
}

