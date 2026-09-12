using System;
using System.Collections.Generic;
using UnityEngine;

public class GameTimer
{
    public float Duration { get; private set; }
    public bool IsLoop { get; private set; }
    public Action OnComplete { get; private set; }
    
    public float CurrentTime { get; private set; }
    public float Ratio => Mathf.Clamp01(CurrentTime / Duration);
    public float RemainingTime => Mathf.Max(0, Duration - CurrentTime);
    private bool isFinished;

    public GameTimer(float duration, bool isLoop, Action onComplete)
    {
        Duration = duration;
        IsLoop = isLoop;
        OnComplete = onComplete;
        CurrentTime = 0f;
        isFinished = false;
    }

    public bool Tick(float dt)
    {
        if (isFinished) return true;

        CurrentTime += dt;
        if (CurrentTime >= Duration)
        {
            UnityEngine.Debug.Log("Timer completed");
            OnComplete?.Invoke();
            if (IsLoop) CurrentTime -= Duration;
            else { isFinished = true; return true; }
        }
        return false;
    }
}

public class TimerManager
{
    private List<GameTimer> timers = new List<GameTimer>();

    public GameTimer AddTimer(float duration, bool isLoop, Action callback)
    {
        var timer = new GameTimer(duration, isLoop, callback);
        timers.Add(timer);
        return timer;
    }

    public void Tick(float dt)
    {
        // Snapshot before ticking: a timer's OnComplete can synchronously end the episode
        // and rebuild this same list (Clear() + AddTimer() for the next episode) before this
        // loop returns. Iterating live indices into a list mutated underneath us would remove
        // whatever now sits at that index — one of the newly-added next-episode timers —
        // instead of the timer that actually just completed, silently orphaning it.
        var snapshot = timers.ToArray();
        foreach (var timer in snapshot)
        {
            if (timer.Tick(dt)) timers.Remove(timer);
        }
    }

    public void Clear() => timers.Clear();
}