using System.Text.Json;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Previews;
using MacroDeck.Ui.Runtime;
using MacroDeck.Ui.Widgets;
using Pomodoro.Timer;

namespace Pomodoro.Widget;

internal static class PomodoroWidgetViewPreviews
{
	private static readonly DateTimeOffset _now = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
	private static readonly DateOnly _today = DateOnly.FromDateTime(_now.Date);

	[UiPreview("Ready", Profile = UiPreviewProfiles.Widget)]
	public static UiElement Ready()
	{
		return Build(PomodoroState.Initial(_now, _today));
	}

	[UiPreview("Focus running", Profile = UiPreviewProfiles.Widget)]
	public static UiElement FocusRunning()
	{
		return Build(Running(PomodoroPhase.Focus, round: 2, elapsedMinutes: 9));
	}

	[UiPreview("Paused", Profile = UiPreviewProfiles.Widget)]
	public static UiElement Paused()
	{
		return Build(Running(PomodoroPhase.Focus, round: 3, elapsedMinutes: 17) with
		{
			Status = PomodoroStatus.Paused,
			ElapsedMs = 17 * 60_000,
			Anchor = _now
		});
	}

	[UiPreview("Short break", Profile = UiPreviewProfiles.Widget)]
	public static UiElement ShortBreak()
	{
		return Build(Running(PomodoroPhase.ShortBreak, round: 1, elapsedMinutes: 2));
	}

	[UiPreview("Long break", Profile = UiPreviewProfiles.Widget)]
	public static UiElement LongBreak()
	{
		return Build(Running(PomodoroPhase.LongBreak, round: 4, elapsedMinutes: 1));
	}

	[UiPreview("Focus done", Profile = UiPreviewProfiles.Widget)]
	public static UiElement FocusDone()
	{
		return Build(Finished(PomodoroPhase.Focus, completed: 3));
	}

	[UiPreview("Break over", Profile = UiPreviewProfiles.Widget)]
	public static UiElement BreakOver()
	{
		return Build(Finished(PomodoroPhase.ShortBreak, completed: 3));
	}

	[UiPreview("Long label, custom accent", Profile = UiPreviewProfiles.Widget)]
	public static UiElement LongLabel()
	{
		return Build(Running(PomodoroPhase.Focus, round: 1, elapsedMinutes: 4),
			"""{"label":"Writing the quarterly report draft","accentColor":"#5b7cfa"}""");
	}

	[UiPreview("Configuration", Profile = UiPreviewProfiles.Config)]
	public static UiElement Configuration()
	{
		return PomodoroConfigView.Build(JsonDocument.Parse("{}").RootElement.Clone());
	}

	private static PomodoroState Running(PomodoroPhase phase, int round, int elapsedMinutes)
	{
		return PomodoroState.Initial(_now, _today) with
		{
			Phase = phase,
			Status = PomodoroStatus.Running,
			Round = round,
			ElapsedMs = 0,
			Anchor = _now.AddMinutes(-elapsedMinutes)
		};
	}

	private static PomodoroState Finished(PomodoroPhase phase, int completed)
	{
		return PomodoroState.Initial(_now, _today) with
		{
			Phase = phase,
			Status = PomodoroStatus.Finished,
			CompletedToday = completed
		};
	}

	private static UiElement Build(PomodoroState state, string data = "{}")
	{
		var element = JsonDocument.Parse(data).RootElement.Clone();
		var face = PomodoroFace.From(state, PomodoroSettings.Parse(element), _now, _today);

		// The preview renderer reads progress on its own fixed clock, not this scenario's, so the countdown is
		// frozen at the scenario's moment to show the time the ring shows.
		face = face with
		{
			Progress = face.Progress with { PositionMs = state.ElapsedAt(_now), Anchor = _now, Rate = 0 }
		};

		return PomodoroWidgetView.Build(new UiState<PomodoroFace>(face), UiWidgetAppearance.Read(element),
			UiWidgetSafeArea.DefaultCornerRadius);
	}
}
