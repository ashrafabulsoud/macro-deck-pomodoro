namespace Pomodoro.Timer;

/// <summary>
/// One widget's timer at an instant. <see cref="ElapsedMs" /> is the time run before <see cref="Anchor" />;
/// while running, the time since the anchor is added on top, so the state never needs a tick to stay right.
/// </summary>
public sealed record PomodoroState(
	PomodoroPhase Phase,
	PomodoroStatus Status,
	int Round,
	long ElapsedMs,
	DateTimeOffset Anchor,
	int CompletedToday,
	DateOnly CompletedDay)
{
	public static PomodoroState Initial(DateTimeOffset now, DateOnly today)
	{
		return new PomodoroState(PomodoroPhase.Focus, PomodoroStatus.Ready, 1, 0, now, 0, today);
	}

	public bool IsRunning => Status == PomodoroStatus.Running;

	public long ElapsedAt(DateTimeOffset now)
	{
		return IsRunning
			? ElapsedMs + Math.Max(0, (long)(now - Anchor).TotalMilliseconds)
			: ElapsedMs;
	}

	public long RemainingMsAt(DateTimeOffset now, PomodoroSettings settings)
	{
		return Status == PomodoroStatus.Finished
			? 0
			: Math.Max(0, settings.DurationMsOf(Phase) - ElapsedAt(now));
	}

	/// <summary>When the running phase runs out, or null when nothing is running.</summary>
	public DateTimeOffset? EndsAt(PomodoroSettings settings)
	{
		return IsRunning
			? Anchor.AddMilliseconds(settings.DurationMsOf(Phase) - ElapsedMs)
			: null;
	}

	/// <summary>The pomodoros completed on <paramref name="today" />; a count from an earlier day reads as 0.</summary>
	public int CompletedOn(DateOnly today)
	{
		return CompletedDay == today ? CompletedToday : 0;
	}
}
