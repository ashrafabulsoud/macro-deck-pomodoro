using NUnit.Framework;
using Pomodoro.Timer;

namespace Pomodoro.Tests;

[TestFixture]
public sealed class PomodoroEngineTests
{
	private static readonly DateTimeOffset _start = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
	private static readonly PomodoroSettings _settings = PomodoroSettings.Default;

	private static DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);

	private static PomodoroState Ready() => PomodoroState.Initial(_start, DayOf(_start));

	private static PomodoroState Elapse(PomodoroState state, DateTimeOffset now, PomodoroSettings? settings = null)
		=> PomodoroEngine.Elapse(state, settings ?? _settings, now, DayOf).State;

	[Test]
	public void A_tap_starts_the_first_focus_phase_and_reports_it()
	{
		var step = PomodoroEngine.Press(Ready(), _settings, _start);

		Assert.That(step.State.Status, Is.EqualTo(PomodoroStatus.Running));
		Assert.That(step.State.Phase, Is.EqualTo(PomodoroPhase.Focus));
		Assert.That(step.Transitions.Single(), Is.EqualTo(
			new PomodoroTransition(PomodoroTransitionKind.PhaseStarted, PomodoroPhase.Focus, 1, 0)));
	}

	[Test]
	public void Pausing_keeps_the_remaining_time_however_long_it_stays_paused()
	{
		var running = PomodoroEngine.Press(Ready(), _settings, _start).State;
		var paused = PomodoroEngine.Press(running, _settings, _start.AddMinutes(10)).State;
		var later = Elapse(paused, _start.AddHours(3));

		Assert.That(later.Status, Is.EqualTo(PomodoroStatus.Paused));
		Assert.That(later.RemainingMsAt(_start.AddHours(3), _settings), Is.EqualTo(15 * 60_000));
	}

	[Test]
	public void Resuming_continues_from_where_it_was_paused()
	{
		var running = PomodoroEngine.Press(Ready(), _settings, _start).State;
		var paused = PomodoroEngine.Press(running, _settings, _start.AddMinutes(10)).State;
		var resumed = PomodoroEngine.Press(paused, _settings, _start.AddMinutes(30)).State;

		Assert.That(resumed.RemainingMsAt(_start.AddMinutes(35), _settings), Is.EqualTo(10 * 60_000));
	}

	[Test]
	public void A_finished_focus_counts_a_pomodoro_and_waits_for_a_tap()
	{
		var running = PomodoroEngine.Press(Ready(), _settings, _start).State;
		var step = PomodoroEngine.Elapse(running, _settings, _start.AddMinutes(25), DayOf);

		Assert.That(step.State.Status, Is.EqualTo(PomodoroStatus.Finished));
		Assert.That(step.State.CompletedToday, Is.EqualTo(1));
		Assert.That(step.Transitions.Select(t => t.Kind), Is.EqualTo(new[]
		{
			PomodoroTransitionKind.PhaseFinished, PomodoroTransitionKind.PomodoroCompleted
		}));
	}

	[Test]
	public void A_focus_phase_does_not_finish_a_moment_early()
	{
		var running = PomodoroEngine.Press(Ready(), _settings, _start).State;

		Assert.That(Elapse(running, _start.AddMinutes(25).AddMilliseconds(-1)).Status,
			Is.EqualTo(PomodoroStatus.Running));
	}

	[Test]
	public void A_tap_after_focus_starts_the_short_break()
	{
		var finished = Elapse(PomodoroEngine.Press(Ready(), _settings, _start).State, _start.AddMinutes(25));
		var step = PomodoroEngine.Press(finished, _settings, _start.AddMinutes(26));

		Assert.That(step.State.Phase, Is.EqualTo(PomodoroPhase.ShortBreak));
		Assert.That(step.State.Status, Is.EqualTo(PomodoroStatus.Running));
		Assert.That(step.Transitions.Single().Kind, Is.EqualTo(PomodoroTransitionKind.PhaseStarted));
	}

	[TestCase(4, PomodoroPhase.Focus, 1, PomodoroPhase.ShortBreak, 1)]
	[TestCase(4, PomodoroPhase.Focus, 4, PomodoroPhase.LongBreak, 4)]
	[TestCase(4, PomodoroPhase.ShortBreak, 2, PomodoroPhase.Focus, 3)]
	[TestCase(4, PomodoroPhase.LongBreak, 4, PomodoroPhase.Focus, 1)]
	[TestCase(1, PomodoroPhase.Focus, 1, PomodoroPhase.LongBreak, 1)]
	public void The_cycle_follows_the_configured_rounds(int rounds, PomodoroPhase phase, int round,
		PomodoroPhase expectedPhase, int expectedRound)
	{
		var settings = _settings with { RoundsBeforeLongBreak = rounds };

		Assert.That(PomodoroEngine.NextOf(phase, round, settings), Is.EqualTo((expectedPhase, expectedRound)));
	}

	[Test]
	public void Auto_start_catches_up_every_phase_missed_while_away()
	{
		var settings = _settings with { AutoStartNext = true };
		var running = PomodoroEngine.Press(Ready(), settings, _start).State;

		// 25 focus + 5 break + 25 focus, then 3 minutes into the second break.
		var state = Elapse(running, _start.AddMinutes(58), settings);

		Assert.That(state.Phase, Is.EqualTo(PomodoroPhase.ShortBreak));
		Assert.That(state.Round, Is.EqualTo(2));
		Assert.That(state.RemainingMsAt(_start.AddMinutes(58), settings), Is.EqualTo(2 * 60_000));
	}

	[Test]
	public void Phases_missed_while_away_are_silent_and_only_the_focus_already_running_counts()
	{
		var settings = _settings with { AutoStartNext = true };
		var running = PomodoroEngine.Press(Ready(), settings, _start).State;

		var step = PomodoroEngine.Elapse(running, settings, _start.AddMinutes(58), DayOf);

		Assert.That(step.State.CompletedToday, Is.EqualTo(1));
		Assert.That(step.Transitions.Single(), Is.EqualTo(
			new PomodoroTransition(PomodoroTransitionKind.PhaseStarted, PomodoroPhase.ShortBreak, 2, 1)));
	}

	[Test]
	public void Shortening_a_phase_below_the_time_already_spent_ends_it_now_and_counts_it()
	{
		var running = PomodoroEngine.Press(Ready(), _settings, _start).State;
		var now = _start.AddMinutes(20);
		var shorter = _settings with { FocusMinutes = 15 };

		var reconfigured = PomodoroEngine.Reconfigure(running, shorter, now).State;
		var step = PomodoroEngine.Elapse(reconfigured, shorter, now, DayOf);

		Assert.That(step.State.Status, Is.EqualTo(PomodoroStatus.Finished));
		Assert.That(step.State.CompletedToday, Is.EqualTo(1));
		Assert.That(step.Transitions.Select(t => t.Kind), Does.Contain(PomodoroTransitionKind.PhaseFinished));
	}

	[Test]
	public void Lengthening_a_running_phase_keeps_the_time_spent()
	{
		var running = PomodoroEngine.Press(Ready(), _settings, _start).State;
		var longer = _settings with { FocusMinutes = 50 };

		var state = PomodoroEngine.Reconfigure(running, longer, _start.AddMinutes(20)).State;

		Assert.That(state.RemainingMsAt(_start.AddMinutes(20), longer), Is.EqualTo(30 * 60_000));
	}

	[Test]
	public void A_phase_end_noticed_a_few_seconds_late_still_counts()
	{
		var running = PomodoroEngine.Press(Ready(), _settings, _start).State;

		var step = PomodoroEngine.Elapse(running, _settings, _start.AddMinutes(25).AddSeconds(20), DayOf);

		Assert.That(step.State.CompletedToday, Is.EqualTo(1));
		Assert.That(step.Transitions, Has.Count.EqualTo(2));
	}

	[Test]
	public void Reset_goes_back_to_the_first_focus_round_and_keeps_the_daily_count()
	{
		var finished = Elapse(PomodoroEngine.Press(Ready(), _settings, _start).State, _start.AddMinutes(25));
		var inBreak = PomodoroEngine.Press(finished, _settings, _start.AddMinutes(25)).State;
		var reset = PomodoroEngine.Reset(inBreak, _start.AddMinutes(27)).State;

		Assert.That(reset.Phase, Is.EqualTo(PomodoroPhase.Focus));
		Assert.That(reset.Status, Is.EqualTo(PomodoroStatus.Ready));
		Assert.That(reset.Round, Is.EqualTo(1));
		Assert.That(reset.CompletedToday, Is.EqualTo(1));
	}

	[Test]
	public void The_daily_count_starts_over_on_a_new_day()
	{
		var yesterday = Ready() with { CompletedToday = 7, CompletedDay = DayOf(_start).AddDays(-1) };
		var running = PomodoroEngine.Press(yesterday, _settings, _start).State;

		Assert.That(running.CompletedOn(DayOf(_start)), Is.Zero);
		Assert.That(Elapse(running, _start.AddMinutes(25)).CompletedToday, Is.EqualTo(1));
	}
}
