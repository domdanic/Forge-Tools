namespace Forge.App;

public sealed class TwitchOutboundChatService(TwitchAuthService broadcaster, TwitchAuthService bot)
{
    public bool PreferBot { get; set; }
    public bool IsUsingBot => PreferBot && broadcaster.Identity is not null && bot.Identity is not null;
    public string? ActiveLogin => IsUsingBot ? bot.Identity?.Login : broadcaster.Identity?.Login;

    public Task SendChatMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        if (broadcaster.Identity is null) throw new InvalidOperationException("Connect the broadcaster Twitch account before sending chat messages.");
        return IsUsingBot
            ? bot.SendChatMessageAsync(message, broadcaster.Identity.UserId, cancellationToken)
            : broadcaster.SendChatMessageAsync(message, cancellationToken);
    }
}
