namespace Pomodoro.Timer;

/// <summary>The Pomodoro state machine. Pure: every input, including the time, is passed in.</summary>
public static class PomodoroEngine
{
	/// <summary>A tap: start, pause, resume, or begin the next phase after one finished.</summary>
	public static PomodoroStep Press(PomodoroState state, PomodoroSettings settings, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(settings);

		return state.Status switch
		{
			PomodoroStatus.Ready => new PomodoroStep(
				state with { Status = PomodoroStatus.Running, ElapsedMs = 0, Anchor = now },
				[Started(state.Phase, state.Round, state.CompletedToday)]),
			PomodoroStatus.Running => PomodoroStep.Only(
				state with { Status = PomodoroStatus.Paused, ElapsedMs = state.ElapsedAt(now), Anchor = now }),
			PomodoroStatus.Paused => PomodoroStep.Only(state with { Status = PomodoroStatus.Running, Anchor = now }),
			_ => StartNext(state, settings, now, run: true)
		};
	}

	/// <summary>A long press: back to the first focus round, not started. The daily count is kept.</summary>
	public static PomodoroStep Reset(PomodoroState state, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(state);

		return PomodoroStep.Only(state with
		{
			Phase = PomodoroPhase.Focus,
			Status = PomodoroStatus.Ready,
			Round = 1,
			ElapsedMs = 0,
			Anchor = now
		});
	}

	/// <summary>
	/// How late a phase end may be noticed and still count as live. Later than this, the computer slept or
	/// the plugin was not running, and nobody was there to see it.
	/// </summary>
	public static readonly TimeSpan CatchUpGrace = TimeSpan.FromMinutes(1);

	/// <summary>
	/// Applies the passage of time. A phase that ran out finishes, and with auto-start the next one starts at
	/// the instant the previous ended, so phases missed while asleep are caught up exactly. Those missed
	/// phases are neither reported nor counted, except that the phase running now is reported as started so
	/// automation follows the timer. Only the phase that was running when time was last applied is counted.
	/// </summary>
	public static PomodoroStep Elapse(
		PomodoroState state,
		PomodoroSettings settings,
		DateTimeOffset now,
		Func<DateTimeOffset, DateOnly> dayOf)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(dayOf);

		var transitions = new List<PomodoroTransition>();
		PomodoroTransition? missedStart = null;
		var first = true;

		while (state.EndsAt(settings) is { } endsAt && endsAt <= now)
		{
			var live = now - endsAt <= CatchUpGrace;
			var day = dayOf(endsAt);
			// The focus that was running when the computer slept was really worked, so it counts; focus phases
			// auto-started while nobody was there do not.
			var counts = state.Phase == PomodoroPhase.Focus && (live || first);
			var completed = state.CompletedOn(day) + (counts ? 1 : 0);
			first = false;

			if (live)
			{
				transitions.Add(new PomodoroTransition(PomodoroTransitionKind.PhaseFinished, state.Phase, state.Round,
					completed));

				if (state.Phase == PomodoroPhase.Focus)
				{
					transitions.Add(new PomodoroTransition(PomodoroTransitionKind.PomodoroCompleted, state.Phase,
						state.Round, completed));
				}
			}

			state = state with
			{
				Status = PomodoroStatus.Finished,
				ElapsedMs = settings.DurationMsOf(state.Phase),
				Anchor = endsAt,
				CompletedToday = completed,
				CompletedDay = day
			};

			if (!settings.AutoStartNext)
			{
				break;
			}

			var next = StartNext(state, settings, endsAt, run: true);
			state = next.State;

			if (live)
			{
				transitions.AddRange(next.Transitions);
				missedStart = null;
			}
			else
			{
				missedStart = next.Transitions.Single();
			}
		}

		if (missedStart is not null && state.IsRunning)
		{
			transitions.Add(missedStart);
		}

		return new PomodoroStep(state, transitions);
	}

	/// <summary>
	/// Applies new settings. A phase the new length has already run past ends now rather than at its old
	/// instant, so it is reported and counted like any other end.
	/// </summary>
	public static PomodoroStep Reconfigure(PomodoroState state, PomodoroSettings settings, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(settings);

		var duration = settings.DurationMsOf(state.Phase);

		return state.Status is PomodoroStatus.Running or PomodoroStatus.Paused && state.ElapsedAt(now) >= duration
			? PomodoroStep.Only(state with { Status = PomodoroStatus.Running, ElapsedMs = duration, Anchor = now })
			: PomodoroStep.Only(state);
	}

	/// <summary>The phase that follows <paramref name="phase" /> in round <paramref name="round" />.</summary>
	public static (PomodoroPhase Phase, int Round) NextOf(PomodoroPhase phase, int round, PomodoroSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		return phase switch
		{
			PomodoroPhase.Focus when round >= settings.RoundsBeforeLongBreak => (PomodoroPhase.LongBreak, round),
			PomodoroPhase.Focus => (PomodoroPhase.ShortBreak, round),
			PomodoroPhase.LongBreak => (PomodoroPhase.Focus, 1),
			_ => (PomodoroPhase.Focus, round + 1)
		};
	}

	private static PomodoroStep StartNext(PomodoroState state, PomodoroSettings settings, DateTimeOffset at, bool run)
	{
		var (phase, round) = NextOf(state.Phase, state.Round, settings);
		var next = state with
		{
			Phase = phase,
			Round = round,
			Status = run ? PomodoroStatus.Running : PomodoroStatus.Ready,
			ElapsedMs = 0,
			Anchor = at
		};

		return run
			? new PomodoroStep(next, [Started(phase, round, state.CompletedToday)])
			: PomodoroStep.Only(next);
	}

	private static PomodoroTransition Started(PomodoroPhase phase, int round, int completedToday)
	{
		return new PomodoroTransition(PomodoroTransitionKind.PhaseStarted, phase, round, completedToday);
	}
}

/// <summary>The state after a step, and what happened on the way there.</summary>
public sealed record PomodoroStep(PomodoroState State, IReadOnlyList<PomodoroTransition> Transitions)
{
	public static PomodoroStep Only(PomodoroState state)
	{
		return new PomodoroStep(state, []);
	}
}
