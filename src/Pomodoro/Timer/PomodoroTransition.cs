namespace Pomodoro.Timer;

public enum PomodoroTransitionKind
{
	PhaseStarted,
	PhaseFinished,
	PomodoroCompleted
}

/// <summary>Something that happened while applying a gesture or the passage of time, published as an event.</summary>
public sealed record PomodoroTransition(PomodoroTransitionKind Kind, PomodoroPhase Phase, int Round, int CompletedToday);
