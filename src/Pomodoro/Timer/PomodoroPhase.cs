namespace Pomodoro.Timer;

public enum PomodoroPhase
{
	Focus,
	ShortBreak,
	LongBreak
}

public enum PomodoroStatus
{
	/// <summary>The phase has not started yet.</summary>
	Ready,
	Running,
	Paused,

	/// <summary>The phase ran out and waits for a tap before the next one starts.</summary>
	Finished
}

public static class PomodoroPhases
{
	public const string FocusId = "focus";
	public const string ShortBreakId = "short-break";
	public const string LongBreakId = "long-break";

	public static string IdOf(PomodoroPhase phase)
	{
		return phase switch
		{
			PomodoroPhase.Focus => FocusId,
			PomodoroPhase.ShortBreak => ShortBreakId,
			_ => LongBreakId
		};
	}
}
