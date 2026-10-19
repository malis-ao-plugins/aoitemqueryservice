using System.Collections.Concurrent;
using AOSharp.Common.GameData;
using AOSharp.Core;
using AOSharp.Core.UI;

/// <summary>
/// Runs work on the game thread. The game client only tolerates calls into it,
/// such as writing to chat or reading a texture, from its own thread, while the
/// web server and the database build run on others; calling in from those can
/// crash the client.
/// </summary>
public sealed class GameThread : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private readonly ConcurrentQueue<Action> _work = new();

    /// <summary>Create on the game thread, as a plugin's Run is.</summary>
    public GameThread() => Game.OnUpdate += OnUpdate;

    public void Dispose() => Game.OnUpdate -= OnUpdate;

    /// <summary>Writes to chat on the next game frame.</summary>
    public void Say(string text, ChatColor color = ChatColor.Gold) =>
        _work.Enqueue(() => Chat.WriteLine(text, color));

    /// <summary>Runs work on the next game frame and waits for its result.</summary>
    public T Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        });

        if (!done.Task.Wait(WaitLimit))
            throw new TimeoutException("The game did not run the request in time.");

        return done.Task.Result;
    }

    private void OnUpdate(object? sender, float deltaTime)
    {
        while (_work.TryDequeue(out var action))
        {
            try { action(); }
            catch { /* a failed chat line must not take the frame down */ }
        }
    }
}
