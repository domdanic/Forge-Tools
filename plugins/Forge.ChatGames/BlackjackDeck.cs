using System.Security.Cryptography;

namespace Forge.ChatGames;

// A fresh single deck belongs to each hand; player and dealer draw from the same pool.
internal sealed class BlackjackDeck
{
    private readonly List<int> _remaining = Enumerable.Range(1, 13)
        .SelectMany(rank => Enumerable.Repeat(rank, 4)).ToList();

    public int Draw()
    {
        if (_remaining.Count == 0) throw new InvalidOperationException("The blackjack deck is empty.");
        var index = RandomNumberGenerator.GetInt32(_remaining.Count);
        var card = _remaining[index];
        _remaining.RemoveAt(index);
        return card;
    }
}
