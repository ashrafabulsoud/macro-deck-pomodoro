using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pomodoro.Timer;

public sealed class PomodoroChangedEventArgs : EventArgs
{
	public PomodoroChangedEventArgs(Guid widgetId)
	{
		WidgetId = widgetId;
	}

	public Guid WidgetId { get; }
}

public sealed class PomodoroTransitionEventArgs : EventArgs
{
	public PomodoroTransitionEventArgs(Guid widgetId, PomodoroSettings settings, PomodoroTransition transition)
	{
		WidgetId = widgetId;
		Settings = settings;
		Transition = transition;
	}

	public Guid WidgetId { get; }

	public PomodoroSettings Settings { get; }

	public PomodoroTransition Transition { get; }
}

/// <summary>
/// Every widget's timer, shared by all sessions that show the widget so each screen agrees, and kept running
/// while no session is open. The integration persists it through <see cref="Export" /> and
/// <see cref="Import" /> so a restart picks up where it left off.
/// </summary>
public sealed class PomodoroTimers : IDisposable
{
	// Ring levels only move while something runs; this is often enough for a 25 minute ring to look
	// continuous, without patching every second.
	internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

	internal static readonly TimeSpan DeletedAfter = TimeSpan.FromMinutes(1);

	private static readonly JsonSerializerOptions _json = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	private readonly TimeProvider _time;
	private readonly Lock _sync = new();
	private readonly Lock _raiseSync = new();
	private readonly ConcurrentQueue<Action> _notifications = new();
	private readonly Dictionary<Guid, Entry> _entries = [];
	private readonly ITimer _timer;

	private Func<IReadOnlySet<Guid>?>? _knownWidgets;
	private DateOnly _lastTickDay;
	private bool _disposed;

	public PomodoroTimers(TimeProvider time)
	{
		ArgumentNullException.ThrowIfNull(time);

		_time = time;
		_lastTickDay = DayOf(time.GetUtcNow());
		_timer = time.CreateTimer(_ => OnTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
	}

	/// <summary>Raised whenever a widget's face may look different, including the periodic ring refresh.</summary>
	public event EventHandler<PomodoroChangedEventArgs>? Changed;

	/// <summary>Raised for every phase start, phase end and completed pomodoro, in the order they happened.</summary>
	public event EventHandler<PomodoroTransitionEventArgs>? Transitioned;

	/// <summary>Raised when state worth keeping across a restart changed. Not raised by the periodic refresh.</summary>
	public event EventHandler? Updated;

	public DateTimeOffset Now => _time.GetUtcNow();

	public DateOnly Today => DayOf(Now);

	/// <summary>
	/// Tells the store which widgets still exist, so a deleted widget's timer stops instead of firing events
	/// forever. The source answers null while it does not know yet; nothing is removed then.
	/// </summary>
	public void UseWidgetDirectory(Func<IReadOnlySet<Guid>?> knownWidgets)
	{
		ArgumentNullException.ThrowIfNull(knownWidgets);

		Volatile.Write(ref _knownWidgets, knownWidgets);
	}

	public PomodoroState Get(Guid widgetId)
	{
		return Snapshot(widgetId).State;
	}

	public (PomodoroState State, PomodoroSettings Settings) Snapshot(Guid widgetId)
	{
		lock (_sync)
		{
			var entry = EntryOf(widgetId);
			return (entry.State, entry.Settings);
		}
	}

	/// <summary>
	/// Applies a widget's saved settings. A running phase keeps its elapsed time against the new length, so
	/// shortening it below what already ran finishes it.
	/// </summary>
	public void Configure(Guid widgetId, PomodoroSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		Update(widgetId, entry =>
		{
			if (entry.Settings == settings)
			{
				return null;
			}

			entry.Settings = settings;
			return PomodoroEngine.Reconfigure(entry.State, settings, Now);
		});
	}

	/// <summary>A session started showing the widget; a widget on screen is never treated as deleted.</summary>
	public void Attach(Guid widgetId)
	{
		lock (_sync)
		{
			EntryOf(widgetId).Sessions++;
		}
	}

	public void Detach(Guid widgetId)
	{
		lock (_sync)
		{
			if (_entries.TryGetValue(widgetId, out var entry) && entry.Sessions > 0)
			{
				entry.Sessions--;
			}
		}
	}

	public void Press(Guid widgetId)
	{
		Update(widgetId, entry => PomodoroEngine.Press(entry.State, entry.Settings, Now));
	}

	public void Reset(Guid widgetId)
	{
		Update(widgetId, entry => PomodoroEngine.Reset(entry.State, Now));
	}

	/// <summary>The state of every widget, as JSON.</summary>
	public string Export()
	{
		lock (_sync)
		{
			var widgets = _entries.ToDictionary(pair => pair.Key,
				pair => new PersistedWidget(pair.Value.Settings, pair.Value.State));

			return JsonSerializer.Serialize(new PersistedTimers(widgets), _json);
		}
	}

	/// <summary>
	/// Restores what <see cref="Export" /> wrote, then catches up on the time that passed while the plugin was
	/// not running. Widgets already known in this process win, so a repeated initialization changes nothing.
	/// </summary>
	public void Import(string json)
	{
		ArgumentNullException.ThrowIfNull(json);

		var restored = TryRead(json) ?? throw new FormatException("The saved Pomodoro timers are not readable.");
		var imported = false;

		lock (_sync)
		{
			foreach (var (widgetId, widget) in restored.Widgets)
			{
				if (_entries.ContainsKey(widgetId))
				{
					continue;
				}

				var entry = new Entry(widget.State, widget.Settings);
				_entries[widgetId] = entry;
				Settle(widgetId, entry, PomodoroStep.Only(entry.State));
				imported = true;
			}

			Rearm();
		}

		if (imported)
		{
			_notifications.Enqueue(() => Updated?.Invoke(this, EventArgs.Empty));
		}

		Drain();
	}

	/// <summary>Whether <paramref name="json" /> is something <see cref="Import" /> can read.</summary>
	public static bool IsReadable(string json)
	{
		return TryRead(json) is not null;
	}

	public void Dispose()
	{
		lock (_sync)
		{
			_disposed = true;
		}

		_timer.Dispose();
	}

	private static PersistedTimers? TryRead(string json)
	{
		try
		{
			return JsonSerializer.Deserialize<PersistedTimers>(json, _json) is { Widgets: not null } timers
				? timers
				: null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private void Update(Guid widgetId, Func<Entry, PomodoroStep?> apply)
	{
		lock (_sync)
		{
			var entry = EntryOf(widgetId);

			if (apply(entry) is not { } step)
			{
				return;
			}

			Settle(widgetId, entry, step);
			Rearm();
			_notifications.Enqueue(() => Changed?.Invoke(this, new PomodoroChangedEventArgs(widgetId)));
			_notifications.Enqueue(() => Updated?.Invoke(this, EventArgs.Empty));
		}

		Drain();
	}

	// Called under the lock. A step may land after a phase ran out but before the timer fired; settle that too.
	private void Settle(Guid widgetId, Entry entry, PomodoroStep step)
	{
		var settled = PomodoroEngine.Elapse(step.State, entry.Settings, Now, DayOf);
		entry.State = settled.State;
		var settings = entry.Settings;

		foreach (var transition in step.Transitions.Concat(settled.Transitions))
		{
			_notifications.Enqueue(() =>
				Transitioned?.Invoke(this, new PomodoroTransitionEventArgs(widgetId, settings, transition)));
		}
	}

	private void OnTick()
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			var changed = PruneDeletedWidgets();
			var today = Today;
			var newDay = today != _lastTickDay;
			_lastTickDay = today;

			foreach (var (widgetId, entry) in _entries)
			{
				if (entry.State.IsRunning)
				{
					var before = entry.State;
					Settle(widgetId, entry, PomodoroStep.Only(entry.State));
					changed |= entry.State.Status != before.Status || entry.State.Phase != before.Phase;
				}

				// A new day resets the count every tile shows, running or not.
				if (entry.State.IsRunning || newDay)
				{
					_notifications.Enqueue(() => Changed?.Invoke(this, new PomodoroChangedEventArgs(widgetId)));
				}
			}

			if (changed)
			{
				_notifications.Enqueue(() => Updated?.Invoke(this, EventArgs.Empty));
			}

			Rearm();
		}

		Drain();
	}

	// Called under the lock.
	private bool PruneDeletedWidgets()
	{
		if (Volatile.Read(ref _knownWidgets)?.Invoke() is not { Count: > 0 } known)
		{
			return false;
		}

		var now = Now;
		var removed = false;

		foreach (var (widgetId, entry) in _entries.ToList())
		{
			// A widget can be shown from somewhere the host's list does not cover, such as another plugin's view.
			if (known.Contains(widgetId) || entry.Sessions > 0)
			{
				entry.MissingSince = null;
				continue;
			}

			// The host's widget list can lag behind a widget that was just placed, so only a widget that stays
			// missing is treated as deleted.
			entry.MissingSince ??= now;

			if (now - entry.MissingSince.Value >= DeletedAfter)
			{
				_entries.Remove(widgetId);
				removed = true;
			}
		}

		return removed;
	}

	// Called under the lock. Fires at the earliest phase end, after the refresh interval while anything runs,
	// and at local midnight while a tile shows today's count.
	private void Rearm()
	{
		if (_disposed)
		{
			return;
		}

		var now = Now;
		var today = DayOf(now);
		TimeSpan? due = null;

		foreach (var entry in _entries.Values)
		{
			if (entry.State.EndsAt(entry.Settings) is { } endsAt)
			{
				var untilEnd = endsAt - now;
				due = Earliest(due, untilEnd < RefreshInterval ? untilEnd : RefreshInterval);
			}
			else if (entry.State.CompletedOn(today) > 0)
			{
				due = Earliest(due, UntilNextDay(now, today));
			}
		}

		_timer.Change(due is { } wait ? (wait < TimeSpan.Zero ? TimeSpan.Zero : wait) : Timeout.InfiniteTimeSpan,
			Timeout.InfiniteTimeSpan);
	}

	private static TimeSpan Earliest(TimeSpan? current, TimeSpan candidate)
	{
		return current is null || candidate < current ? candidate : current.Value;
	}

	private TimeSpan UntilNextDay(DateTimeOffset now, DateOnly today)
	{
		var midnight = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
		var offset = _time.LocalTimeZone.GetUtcOffset(midnight);

		return new DateTimeOffset(midnight, offset) - now;
	}

	// Notifications queue under the store lock and are raised here, outside it and one thread at a time, so
	// listeners see them in the order they happened even when a tap and a timer tick race.
	private void Drain()
	{
		lock (_raiseSync)
		{
			while (_notifications.TryDequeue(out var notify))
			{
				notify();
			}
		}
	}

	private Entry EntryOf(Guid widgetId)
	{
		if (!_entries.TryGetValue(widgetId, out var entry))
		{
			entry = new Entry(PomodoroState.Initial(Now, Today), PomodoroSettings.Default);
			_entries[widgetId] = entry;
		}

		return entry;
	}

	private DateOnly DayOf(DateTimeOffset instant)
	{
		return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, _time.LocalTimeZone).DateTime);
	}

	private sealed class Entry
	{
		public Entry(PomodoroState state, PomodoroSettings settings)
		{
			State = state;
			Settings = settings;
		}

		public PomodoroState State { get; set; }

		public PomodoroSettings Settings { get; set; }

		public DateTimeOffset? MissingSince { get; set; }

		public int Sessions { get; set; }
	}

	private sealed record PersistedWidget(PomodoroSettings Settings, PomodoroState State);

	private sealed record PersistedTimers(Dictionary<Guid, PersistedWidget> Widgets);
}
