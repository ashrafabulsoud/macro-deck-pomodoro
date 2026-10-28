using System.Globalization;
using MacroDeck.Ui.Model.References;
using Pomodoro.Timer;

namespace Pomodoro.Widget;

/// <summary>What a tile shows. Equal faces produce no patch, so the periodic refresh costs nothing while paused.</summary>
internal sealed record PomodoroFace(
	PomodoroPhase Phase,
	PomodoroStatus Status,
	int Round,
	int Rounds,
	int CompletedToday,
	double Level,
	UiProgressReference Progress,
	string StaticTime,
	string? Label)
{
	// The reader floors whole seconds; carrying 999 ms more than the phase runs makes the readout round up,
	// so it shows 25:00 at the start and reaches 0:00 only at the very end.
	private const long RoundUpMs = 999;

	public bool IsFinished => Status == PomodoroStatus.Finished;

	public bool IsPaused => Status == PomodoroStatus.Paused;

	public static PomodoroFace From(PomodoroState state, PomodoroSettings settings, DateTimeOffset now, DateOnly today)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(settings);

		var duration = settings.DurationMsOf(state.Phase);
		var remaining = state.RemainingMsAt(now, settings);

		return new PomodoroFace(
			state.Phase,
			state.Status,
			state.Round,
			settings.RoundsBeforeLongBreak,
			state.CompletedOn(today),
			// Rounded so the face changes, and patches, only when the ring visibly moves.
			Math.Round(duration > 0 ? (double)remaining / duration : 0, 3),
			new UiProgressReference
			{
				PositionMs = state.ElapsedMs,
				Anchor = state.Anchor,
				DurationMs = duration + RoundUpMs,
				Rate = state.IsRunning ? null : 0
			},
			FormatMs(remaining),
			settings.Label);
	}

	public static string FormatMs(long milliseconds)
	{
		var span = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, milliseconds) / 1000d));

		return span.TotalHours >= 1
			? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
			: string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}:{span.Seconds:00}");
	}
}
