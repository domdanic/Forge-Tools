using Forge.PluginSdk;
using System.Security.Cryptography;
using System.Text.Json;

namespace Forge.ChatGames;

public sealed class ChatGamesPlugin : IForgePlugin
{
    private IForgeContext? _context;
    private IDisposable? _subscription;
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EconomyState _state = new();
    private readonly Dictionary<string, BlackjackGame> _blackjack = new(StringComparer.Ordinal);
    private string EconomyPath => Path.Combine(_context!.DataDirectory, "economy.json");

    public Task InitializeAsync(IForgeContext context, CancellationToken cancellationToken)
    {
        _context = context;
        try { _state = JsonSerializer.Deserialize<EconomyState>(File.ReadAllText(EconomyPath)) ?? new(); } catch { _state = new(); }
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _subscription = _context!.Events.Subscribe<TwitchChatMessage>(HandleMessageAsync);
        _worker = EarnPointsAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose(); _subscription = null;
        if (_lifetime is not null)
        {
            _lifetime.Cancel();
            if (_worker is not null) try { await _worker.WaitAsync(cancellationToken); } catch (OperationCanceledException) { }
            _lifetime.Dispose(); _lifetime = null;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var (userId, game) in _blackjack) GetPlayer(userId).Balance += game.Bet;
            _blackjack.Clear(); Save();
        }
        finally { _gate.Release(); }
    }

    private async Task HandleMessageAsync(TwitchChatMessage message)
    {
        if (!_context!.Settings.Get("economyEnabled", true)) return;
        await _gate.WaitAsync();
        try
        {
            var player = GetPlayer(message.UserId, message.UserLogin, message.UserName);
            player.LastSeen = DateTimeOffset.UtcNow;
            var parts = message.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            var command = parts[0];
            string? response = null;
            if (Matches(command, "pointsCommand", "!points")) response = $"@{message.UserLogin}, you have {player.Balance:N0} {PointsName}.";
            else if (Matches(command, "leaderboardCommand", "!top")) response = Leaderboard();
            else if (message.IsBroadcaster || message.IsModerator)
            {
                if (command.Equals("!pointsadd", StringComparison.OrdinalIgnoreCase)) response = AddPoints(parts);
            }
            if (response is null && _context.Settings.Get("slotsEnabled", true) && Matches(command, "slotsCommand", "!slots")) response = PlaySlots(message.UserId, parts);
            if (response is null && _context.Settings.Get("rouletteEnabled", true) && Matches(command, "rouletteCommand", "!roulette")) response = PlayRoulette(message.UserId, parts);
            if (response is null && _context.Settings.Get("blackjackEnabled", true))
            {
                if (Matches(command, "blackjackCommand", "!blackjack")) response = StartBlackjack(message.UserId, parts);
                else if (Matches(command, "hitCommand", "!hit")) response = Hit(message.UserId);
                else if (Matches(command, "standCommand", "!stand")) response = Stand(message.UserId);
            }
            if (response is null && _context.Settings.Get("bossEnabled", true) && Matches(command, "bossCommand", "!boss")) response = AttackBoss(message.UserId, player, parts);
            Save();
            if (response is not null) await SendAsync(response);
        }
        catch (Exception ex) { WriteStatus(ex.Message); }
        finally { _gate.Release(); }
    }

    private string PlaySlots(string userId, string[] parts)
    {
        var player = GetPlayer(userId);
        var bet = Bet(parts, 1, Int("slotsDefaultBet", 10, 1, 1_000_000), Int("slotsMaxBet", 1000, 1, 1_000_000));
        if (bet <= 0 || player.Balance < bet) return $"@{player.Login}, you need {bet:N0} {PointsName} to spin.";
        player.Balance -= bet;
        var win = RandomNumberGenerator.GetInt32(100) < Int("slotsWinPercent", 35, 0, 100);
        if (!win) return $"@{player.Login} spun 🍒 🍋 ⭐ and lost {bet:N0} {PointsName}.";
        var jackpot = RandomNumberGenerator.GetInt32(100) < 5;
        var multiplier = jackpot ? Math.Max(5, Int("slotsPayout", 2, 1, 100) * 5) : Int("slotsPayout", 2, 1, 100);
        var payout = checked(bet * multiplier); player.Balance += payout;
        return jackpot ? $"JACKPOT! @{player.Login} spun ⭐ ⭐ ⭐ and won {payout:N0} {PointsName}!" : $"@{player.Login} spun 🍒 🍒 🍒 and won {payout:N0} {PointsName}!";
    }

    private string PlayRoulette(string userId, string[] parts)
    {
        var player = GetPlayer(userId);
        if (parts.Length < 3) return $"@{player.Login}, use {_context!.Settings.Get("rouletteCommand", "!roulette")} red 10, odd 10, or 17 10.";
        var choice = parts[1].ToLowerInvariant();
        var bet = Bet(parts, 2, 0, Int("rouletteMaxBet", 1000, 1, 1_000_000));
        if (bet <= 0 || player.Balance < bet) return $"@{player.Login}, that bet is invalid or your balance is too low.";
        var validNumber = int.TryParse(choice, out var selected) && selected is >= 0 and <= 36;
        if (!validNumber && choice is not ("red" or "black" or "odd" or "even")) return $"@{player.Login}, choose red, black, odd, even, or a number from 0–36.";
        player.Balance -= bet;
        var roll = RandomNumberGenerator.GetInt32(37);
        var red = RedNumbers.Contains(roll);
        var won = validNumber ? roll == selected : roll != 0 && choice switch { "red" => red, "black" => !red, "odd" => roll % 2 == 1, "even" => roll % 2 == 0, _ => false };
        var payout = won ? checked(bet * (validNumber ? 36 : 2)) : 0; player.Balance += payout;
        return $"Roulette landed on {roll} {(roll == 0 ? "green" : red ? "red" : "black")}. @{player.Login} {(won ? $"won {payout:N0}" : $"lost {bet:N0}")} {PointsName}.";
    }

    private string StartBlackjack(string userId, string[] parts)
    {
        var player = GetPlayer(userId);
        if (_blackjack.ContainsKey(userId)) return $"@{player.Login}, finish your current hand with {_context!.Settings.Get("hitCommand", "!hit")} or {_context.Settings.Get("standCommand", "!stand")}.";
        var bet = Bet(parts, 1, 10, Int("blackjackMaxBet", 1000, 1, 1_000_000));
        if (bet <= 0 || player.Balance < bet) return $"@{player.Login}, that bet is invalid or your balance is too low.";
        player.Balance -= bet;
        var game = new BlackjackGame { Bet = bet, Player = [Card(), Card()], Dealer = [Card(), Card()] };
        _blackjack[userId] = game;
        if (Score(game.Player) == 21) return ResolveBlackjack(userId, game, natural: true);
        return $"@{player.Login}: {Hand(game.Player)} ({Score(game.Player)}). Dealer shows {game.Dealer[0]}. Use {_context!.Settings.Get("hitCommand", "!hit")} or {_context.Settings.Get("standCommand", "!stand")}.";
    }

    private string Hit(string userId)
    {
        var player = GetPlayer(userId);
        if (!_blackjack.TryGetValue(userId, out var game)) return $"@{player.Login}, start a hand first.";
        game.Player.Add(Card());
        return Score(game.Player) >= 21 ? ResolveBlackjack(userId, game, false) : $"@{player.Login}: {Hand(game.Player)} ({Score(game.Player)}). Hit or stand?";
    }

    private string Stand(string userId) => _blackjack.TryGetValue(userId, out var game) ? ResolveBlackjack(userId, game, false) : $"@{GetPlayer(userId).Login}, start a hand first.";

    private string ResolveBlackjack(string userId, BlackjackGame game, bool natural)
    {
        var player = GetPlayer(userId);
        while (Score(game.Dealer) < 17) game.Dealer.Add(Card());
        var yours = Score(game.Player); var dealer = Score(game.Dealer);
        long payout; string result;
        if (yours > 21) { payout = 0; result = "busted"; }
        else if (dealer > 21 || yours > dealer) { payout = natural ? game.Bet * 5 / 2 : game.Bet * 2; result = natural ? "hit blackjack" : "won"; }
        else if (yours == dealer) { payout = game.Bet; result = "pushed"; }
        else { payout = 0; result = "lost"; }
        player.Balance += payout; _blackjack.Remove(userId);
        return $"@{player.Login} {result}: {Hand(game.Player)} ({yours}) vs dealer {Hand(game.Dealer)} ({dealer}). Payout: {payout:N0} {PointsName}.";
    }

    private string AttackBoss(string userId, Player player, string[] parts)
    {
        var maxHp = Int("bossHitPoints", 500, 10, 1_000_000);
        if (_state.BossHp <= 0 || _state.BossMaxHp != maxHp) { _state.BossHp = maxHp; _state.BossMaxHp = maxHp; _state.BossDamage.Clear(); _state.BossLastAttack.Clear(); }
        if (parts.Length > 1 && parts[1].Equals("status", StringComparison.OrdinalIgnoreCase)) return $"The boss has {_state.BossHp:N0}/{maxHp:N0} HP. Use {_context!.Settings.Get("bossCommand", "!boss")} to attack!";
        var cooldown = TimeSpan.FromSeconds(Int("bossCooldownSeconds", 30, 5, 3600));
        if (_state.BossLastAttack.TryGetValue(userId, out var last) && DateTimeOffset.UtcNow - last < cooldown) return $"@{player.Login}, your attack is still cooling down.";
        _state.BossLastAttack[userId] = DateTimeOffset.UtcNow;
        var damage = RandomNumberGenerator.GetInt32(8, 26); _state.BossHp = Math.Max(0, _state.BossHp - damage);
        _state.BossDamage[userId] = _state.BossDamage.GetValueOrDefault(userId) + damage;
        if (_state.BossHp > 0) return $"@{player.Login} dealt {damage} damage! Boss HP: {_state.BossHp:N0}/{maxHp:N0}.";
        var pool = Int("bossReward", 1000, 1, 10_000_000); var totalDamage = Math.Max(1, _state.BossDamage.Values.Sum());
        foreach (var entry in _state.BossDamage) GetPlayer(entry.Key).Balance += Math.Max(1, pool * entry.Value / totalDamage);
        var heroes = _state.BossDamage.Count; _state.BossHp = 0;
        return $"Boss defeated by {heroes} hero(es)! The {pool:N0} {PointsName} reward pool was split by damage dealt.";
    }

    private async Task EarnPointsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            if (!_context!.Settings.Get("economyEnabled", true)) continue;
            var interval = TimeSpan.FromMinutes(Int("earnMinutes", 10, 1, 1440));
            if (DateTimeOffset.UtcNow - _state.LastEarnedAt < interval) continue;
            IReadOnlyList<TwitchChatter> chatters = [];
            if (_context.Settings.Get("includeConnectedChatters", true) && _context.Connections.Twitch.IsConnected)
            {
                try { chatters = await _context.Connections.Twitch.GetChattersAsync(cancellationToken); }
                catch (Exception ex) { WriteStatus("Could not refresh connected chatters; recently active chat users will still earn points. " + ex.GetBaseException().Message); }
            }
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (DateTimeOffset.UtcNow - _state.LastEarnedAt < interval) continue;
                var excluded = _context.Settings.Get("excludedPointUsers", "").Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var chatter in chatters.Where(chatter => !string.IsNullOrWhiteSpace(chatter.UserId) && !excluded.Contains(chatter.UserLogin)))
                {
                    var player = GetPlayer(chatter.UserId, chatter.UserLogin, chatter.UserName);
                    player.LastSeen = DateTimeOffset.UtcNow;
                }
                var active = TimeSpan.FromMinutes(Int("activeMinutes", 20, 1, 1440));
                var amount = Int("earnAmount", 5, 0, 1_000_000);
                foreach (var player in _state.Players.Values.Where(player => DateTimeOffset.UtcNow - player.LastSeen <= active)) player.Balance += amount;
                _state.LastEarnedAt = DateTimeOffset.UtcNow; Save();
            }
            finally { _gate.Release(); }
        }
    }

    private string Leaderboard()
    {
        var leaders = _state.Players.Values.OrderByDescending(player => player.Balance).Take(5).Select((player, index) => $"{index + 1}. {player.DisplayName}: {player.Balance:N0}");
        return $"Top {PointsName}: " + string.Join(" | ", leaders);
    }
    private string AddPoints(string[] parts)
    {
        if (parts.Length < 3 || !long.TryParse(parts[2], out var amount)) return "Mods: use !pointsadd @user amount.";
        var login = parts[1].TrimStart('@'); var target = _state.Players.Values.FirstOrDefault(player => player.Login.Equals(login, StringComparison.OrdinalIgnoreCase));
        if (target is null) return $"I haven't seen {login} in chat yet.";
        target.Balance = Math.Max(0, target.Balance + amount); return $"@{target.Login} now has {target.Balance:N0} {PointsName}.";
    }
    private Player GetPlayer(string userId, string? login = null, string? displayName = null)
    {
        if (!_state.Players.TryGetValue(userId, out var player)) _state.Players[userId] = player = new() { Balance = Int("startingBalance", 100, 0, 10_000_000) };
        if (!string.IsNullOrWhiteSpace(login)) player.Login = login;
        if (!string.IsNullOrWhiteSpace(displayName)) player.DisplayName = displayName;
        if (player.DisplayName.Length == 0) player.DisplayName = player.Login.Length == 0 ? "viewer" : player.Login;
        return player;
    }
    private async Task SendAsync(string text) { await _context!.Connections.Twitch.SendChatMessageAsync(text.Length <= 500 ? text : text[..500]); WriteStatus(text); }
    private bool Matches(string actual, string setting, string fallback) => actual.Equals(_context!.Settings.Get(setting, fallback).Trim(), StringComparison.OrdinalIgnoreCase);
    private string PointsName => _context!.Settings.Get("pointsName", "Sparks").Trim() is { Length: > 0 } name ? name : "points";
    private int Int(string key, int fallback, int min, int max) => Math.Clamp(int.TryParse(_context!.Settings.Get(key, fallback.ToString()), out var parsed) ? parsed : fallback, min, max);
    private static long Bet(string[] parts, int index, int fallback, int max) => Math.Clamp(parts.Length > index && long.TryParse(parts[index], out var parsed) ? parsed : fallback, 0, max);
    private static int Card() => RandomNumberGenerator.GetInt32(1, 14);
    private static int Score(IEnumerable<int> cards) { var values = cards.Select(card => card == 1 ? 11 : Math.Min(card, 10)).ToList(); var score = values.Sum(); foreach (var ace in cards.Where(card => card == 1)) if (score > 21) score -= 10; return score; }
    private static string Hand(IEnumerable<int> cards) => string.Join(" ", cards.Select(card => card switch { 1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => card.ToString() }));
    private void Save() { var temporary = EconomyPath + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, EconomyPath, true); }
    private void WriteStatus(string message) => File.WriteAllText(Path.Combine(_context!.DataDirectory, "status.json"), JsonSerializer.Serialize(new { message, at = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
    public async ValueTask DisposeAsync() { if (_lifetime is not null) await StopAsync(CancellationToken.None); _gate.Dispose(); }

    private static readonly HashSet<int> RedNumbers = [1,3,5,7,9,12,14,16,18,19,21,23,25,27,30,32,34,36];
    private sealed class BlackjackGame { public long Bet { get; set; } public List<int> Player { get; set; } = []; public List<int> Dealer { get; set; } = []; }
    private sealed class Player { public string Login { get; set; } = ""; public string DisplayName { get; set; } = ""; public long Balance { get; set; } public DateTimeOffset LastSeen { get; set; } }
    private sealed class EconomyState
    {
        public Dictionary<string, Player> Players { get; set; } = [];
        public DateTimeOffset LastEarnedAt { get; set; } = DateTimeOffset.UtcNow;
        public int BossHp { get; set; }
        public int BossMaxHp { get; set; }
        public Dictionary<string, int> BossDamage { get; set; } = [];
        public Dictionary<string, DateTimeOffset> BossLastAttack { get; set; } = [];
    }
}
