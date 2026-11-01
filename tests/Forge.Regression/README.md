# Regression checks

Run from the repository root:

```powershell
dotnet run --project tests/Forge.Regression/Forge.Regression.csproj
```

The executable exits with a failure if any check fails. It runs Avalonia headlessly, with no live Twitch or OBS connections (OBS tests use a local simulated WebSocket server), and verifies:

- Clicking Edit scrolls both saved-rule forms into view.
- Closed apps remain selected through editing and refreshing, and edited rules emit the save event without creating duplicates.
- Capture edits retain alternative OBS sources and the no-source choice.
- A full blackjack deck contains exactly four cards of each rank and cannot refill mid-hand.
- Player and dealer share a finite deck; the dealer keeps its original cards and uses consistent labels in opening and final messages.

Each blackjack hand starts a fresh deck. Simultaneous viewers have independent hands and decks.

OBS checks cover startup while unavailable, reconnecting after abrupt and graceful disconnects, failing in-flight requests, stopping queued retries, disabling automatic recovery, requiring successful identification, and shutdown cleanup.
